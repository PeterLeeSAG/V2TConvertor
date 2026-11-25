using System.Diagnostics;
using System.Globalization;

namespace Qwen3AsrOllama.Tests;

public sealed class VoiceToTextIntegrationTests
{
    private const string VoiceUrl =
        "https://www.samplefiles.online/files/sample-speech-short.wav";
    private const string Endpoint = "http://localhost:11434";
    private const string Model = "frozenlab/qwen3-asr:1.7b";

    [Fact]
    public async Task M4aFileIsDiscoveredDecodedAndTranscribed()
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        byte[] voice = await http.GetByteArrayAsync(VoiceUrl);
        Assert.True(voice.Length > 10_000, "The downloaded voice fixture was unexpectedly small.");

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"qwen3-asr-{Guid.NewGuid():N}");
        string wavPath = Path.Combine(tempDirectory, "online-voice.wav");
        string m4aPath = Path.Combine(tempDirectory, "online-voice.M4A");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            await File.WriteAllBytesAsync(wavPath, voice);
            await ConvertToM4aAsync(wavPath, m4aPath);
            File.Delete(wavPath);

            var discovered = Program.DiscoverFiles(tempDirectory).ToList();
            Assert.Equal(new[] { m4aPath }, discovered);
            Assert.True(Program.IsSupported(m4aPath));

            byte[] pcm = Program.LoadAs16kMonoPcm(m4aPath);
            Assert.True(pcm.Length > 16_000 * 2, "ffmpeg did not decode the M4A audio.");

            var chunks = Program.BuildTimelineChunks(pcm);
            Assert.NotEmpty(chunks);

            var transcriptions = new List<string>(chunks.Count);
            foreach (var chunk in chunks)
            {
                string wavBase64 = Program.EncodeToWavBase64(chunk.Pcm);
                string text = await Program.TranscribeChunkAsync(
                    http,
                    Endpoint,
                    Model,
                    wavBase64);

                if (!string.IsNullOrWhiteSpace(text))
                    transcriptions.Add(text);
            }

            string transcription = string.Join(" ", transcriptions);
            Assert.True(
                transcription.Length > 20,
                "Ollama returned no usable transcription text for the M4A fixture.");

            string normalized = transcription.ToLower(CultureInfo.InvariantCulture);
            Assert.Contains("sample", normalized);
            Assert.Contains("voice", normalized);
            Assert.Contains("recognition", normalized);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static async Task ConvertToM4aAsync(string wavPath, string m4aPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Program.ResolveFfmpegExecutable(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(wavPath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("128k");
        psi.ArgumentList.Add(m4aPath);

        using var process = new Process { StartInfo = psi };
        process.Start();

        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg could not create the M4A fixture: {error.Trim()}");

        Assert.True(File.Exists(m4aPath), "ffmpeg did not create the M4A fixture.");
    }

    [Fact]
    public async Task OnlineVoiceFileTranscribesWithQwen3Asr()
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        byte[] voice = await http.GetByteArrayAsync(VoiceUrl);
        Assert.True(voice.Length > 10_000, "The downloaded voice fixture was unexpectedly small.");

        string fixturePath = Path.Combine(
            Path.GetTempPath(),
            $"qwen3-asr-{Guid.NewGuid():N}.wav");

        try
        {
            await File.WriteAllBytesAsync(fixturePath, voice);

            byte[] pcm = Program.LoadAs16kMonoPcm(fixturePath);
            Assert.True(pcm.Length > 16_000 * 2, "ffmpeg did not decode any PCM audio.");

            var chunks = Program.BuildTimelineChunks(pcm);
            Assert.NotEmpty(chunks);

            var transcriptions = new List<string>(chunks.Count);
            foreach (var chunk in chunks)
            {
                string wavBase64 = Program.EncodeToWavBase64(chunk.Pcm);
                string text = await Program.TranscribeChunkAsync(
                    http,
                    Endpoint,
                    Model,
                    wavBase64);

                if (!string.IsNullOrWhiteSpace(text))
                    transcriptions.Add(text);
            }

            string transcription = string.Join(
                " ",
                transcriptions);

            Assert.True(
                transcription.Length > 20,
                "Ollama returned no usable transcription text.");

            string normalized = transcription.ToLower(CultureInfo.InvariantCulture);
            Assert.Contains("sample", normalized);
            Assert.Contains("voice", normalized);
            Assert.Contains("recognition", normalized);
        }
        finally
        {
            if (File.Exists(fixturePath))
                File.Delete(fixturePath);
        }
    }
}
