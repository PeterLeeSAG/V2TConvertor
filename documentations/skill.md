# Qwen3-ASR Ollama Batch Transcriber (C# / .NET 8+)

Below is a complete, zero-NuGet console application implementing the described pipeline.

## `Qwen3AsrOllama.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>qwen3-asr</AssemblyName>
    <RootNamespace>Qwen3AsrOllama</RootNamespace>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

## `Program.cs`

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Qwen3AsrOllama;

internal static class Program
{
    // ---- Audio constants ---------------------------------------------------
    private const int SampleRate = 16_000;
    private const int BytesPerSample = 2;          // s16le
    private const int MaxChunkMs = 55_000;         // Ollama ~60s context limit
    private const int MinChunkMs = 15_000;         // never cut a chunk shorter
    private const int WindowMs = 50;               // RMS window size

    private static readonly string[] SupportedExtensions =
        { ".m4a", ".mp3", ".wav", ".mp4", ".ogg", ".flac", ".aac", ".wma" };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static int _lastProgressLen;

    // -----------------------------------------------------------------------
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "Usage: qwen3-asr <file-or-directory> [--model <name>] [--endpoint <url>]");
            Console.Error.WriteLine(
                "  default model    : frozenlab/qwen3-asr:1.7b");
            Console.Error.WriteLine(
                "  default endpoint : http://localhost:11434");
            return 1;
        }

        string input = args[0];
        string model = "frozenlab/qwen3-asr:1.7b";
        string endpoint = "http://localhost:11434";

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length: model = args[++i]; break;
                case "--endpoint" when i + 1 < args.Length: endpoint = args[++i]; break;
                default:
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return 1;
            }
        }

        List<string> files;
        try
        {
            files = DiscoverFiles(input).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Discovery failed: {ex.Message}");
            return 1;
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine($"No supported audio files found under '{input}'.");
            return 1;
        }

        Console.WriteLine($"Model    : {model}");
        Console.WriteLine($"Endpoint : {endpoint}");
        Console.WriteLine($"Files    : {files.Count}");
        Console.WriteLine(new string('-', 60));

        int failures = 0;
        var totalSw = Stopwatch.StartNew();

        for (int f = 0; f < files.Count; f++)
        {
            string file = files[f];
            Console.WriteLine();
            Console.WriteLine($"[{f + 1}/{files.Count}] {Path.GetFileName(file)}");
            var sw = Stopwatch.StartNew();

            try
            {
                // 1) Decode to raw 16 kHz / 16-bit / mono PCM
                Console.Write("  Decoding audio via ffmpeg... ");
                byte[] pcm = LoadAs16kMonoPcm(file);
                double durationSec = (double)pcm.Length / (SampleRate * BytesPerSample);
                Console.WriteLine($"{durationSec:0.0}s");

                if (pcm.Length == 0)
                {
                    Console.WriteLine("  Skipped: empty audio stream.");
                    continue;
                }

                // 2) Silence-aware chunking
                var chunks = BuildTimelineChunks(pcm);
                Console.WriteLine($"  Chunks  : {chunks.Count} " +
                                  $"({string.Join(", ", chunks.Select(c => $"{c.EndMs - c.StartMs}ms"))})");

                // 3) Transcribe each chunk
                _lastProgressLen = 0;
                var segments = new List<Segment>(chunks.Count);

                for (int i = 0; i < chunks.Count; i++)
                {
                    Chunk chunk = chunks[i];
                    string b64 = EncodeToWavBase64(chunk.Pcm);
                    string text = await TranscribeChunkAsync(Http, endpoint, model, b64);
                    segments.Add(new Segment(chunk.StartMs, chunk.EndMs, text));
                    DrawProgressBar(i + 1, chunks.Count, sw.Elapsed, chunk.EndMs);
                }
                Console.WriteLine();

                // 4) Write SRT alongside the source
                string srtPath = Path.ChangeExtension(file, ".srt");
                WriteSrt(srtPath, segments);
                Console.WriteLine($"  Output  : {srtPath}  ({segments.Count(s => !string.IsNullOrWhiteSpace(s.Text))} lines, {sw.Elapsed:hh\\:mm\\:ss})");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine();
                Console.WriteLine($"  ERROR: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"Done in {totalSw.Elapsed:hh\\:mm\\:ss}. " +
                          $"{files.Count - failures} ok, {failures} failed.");
        return failures == 0 ? 0 : 2;
    }

    // =======================================================================
    // 1. Batch discovery
    // =======================================================================
    private static IEnumerable<string> DiscoverFiles(string input)
    {
        if (File.Exists(input))
        {
            if (IsSupported(input)) yield return Path.GetFullPath(input);
            else throw new NotSupportedException($"Unsupported extension: {Path.GetExtension(input)}");
            yield break;
        }

        if (!Directory.Exists(input))
            throw new DirectoryNotFoundException(input);

        foreach (string file in Directory.EnumerateFiles(input, "*", SearchOption.TopDirectoryOnly)
                                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (IsSupported(file)) yield return Path.GetFullPath(file);
        }
    }

    private static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // =======================================================================
    // 2. Cross-platform decoding: ffmpeg -> raw PCM (16 kHz, s16le, mono)
    // =======================================================================
    private static byte[] LoadAs16kMonoPcm(string inputPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-ar");       psi.ArgumentList.Add(SampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-ac");       psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f");        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("pipe:1");

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Drain stderr concurrently to avoid pipe deadlock.
        Task<string> errTask = proc.StandardError.ReadToEndAsync();

        using var ms = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit();
        string stderr = errTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg exited with {proc.ExitCode}: {stderr.Trim()}");

        return ms.ToArray();
    }

    // =======================================================================
    // 3. Silence-aware timeline chunking
    // =======================================================================
    private static List<Chunk> BuildTimelineChunks(byte[] pcm)
    {
        int totalSamples = pcm.Length / BytesPerSample;
        int windowSamples = SampleRate * WindowMs / 1000;   // 800
        int maxSamples    = SampleRate * MaxChunkMs / 1000; // 880 000
        int minSamples    = SampleRate * MinChunkMs / 1000; // 240 000

        var chunks = new List<Chunk>();
        int pos = 0;

        while (pos < totalSamples)
        {
            int remaining = totalSamples - pos;
            int end;

            if (remaining <= maxSamples)
            {
                end = totalSamples;                          // tail chunk
            }
            else
            {
                int limit = pos + maxSamples;
                int floor = pos + minSamples;

                // Scan backwards from the 55s mark to the 15s mark and pick
                // the quietest 50 ms window (RMS) to avoid mid-word cuts.
                int bestSplit = limit;
                double bestEnergy = double.MaxValue;

                for (int w = limit; w >= floor; w -= windowSamples)
                {
                    int winStart = Math.Max(pos, w - windowSamples);
                    if (winStart >= w) break;

                    double energy = Rms(pcm, winStart, w);
                    if (energy < bestEnergy)
                    {
                        bestEnergy = energy;
                        bestSplit = w;
                    }
                }

                end = bestSplit;
            }

            int sliceBytes = (end - pos) * BytesPerSample;
            var slice = new byte[sliceBytes];
            Buffer.BlockCopy(pcm, pos * BytesPerSample, slice, 0, sliceBytes);

            chunks.Add(new Chunk(
                StartMs: pos * 1000 / SampleRate,
                EndMs:   end * 1000 / SampleRate,
                Pcm:     slice));

            pos = end;
        }

        return chunks;
    }

    private static double Rms(byte[] pcm, int startSample, int endSample)
    {
        double sum = 0;
        int count = 0;
        for (int i = startSample; i < endSample; i++)
        {
            short s = BitConverter.ToInt16(pcm, i * BytesPerSample);
            sum += (double)s * s;
            count++;
        }
        return count == 0 ? 0.0 : Math.Sqrt(sum / count);
    }

    // =======================================================================
    // 4. In-memory WAV encoding (44-byte RIFF header + PCM)
    // =======================================================================
    private static string EncodeToWavBase64(byte[] pcm)
    {
        const int headerSize = 44;
        int dataLen = pcm.Length;
        int byteRate = SampleRate * BytesPerSample; // mono

        var wav = new byte[headerSize + dataLen];
        using var ms = new MemoryStream(wav, writable: true);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataLen);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                    // fmt chunk size
        bw.Write((short)1);              // PCM
        bw.Write((short)1);              // channels
        bw.Write(SampleRate);
        bw.Write(byteRate);
        bw.Write((short)BytesPerSample); // block align
        bw.Write((short)16);             // bits per sample
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);
        bw.Write(pcm);
        bw.Flush();

        return Convert.ToBase64String(wav);
    }

    // =======================================================================
    // 5. Ollama /api/chat inference
    // =======================================================================
    private static async Task<string> TranscribeChunkAsync(
        HttpClient http, string endpoint, string model, string wavBase64)
    {
        var payload = new
        {
            model,
            stream = false,
            messages = new object[]
            {
                new { role = "user", content = "", images = new[] { wavBase64 } }
            }
        };

        string json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        string url = $"{endpoint.TrimEnd('/')}/api/chat";
        using HttpResponseMessage resp = await http.PostAsync(url, content);

        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Ollama returned {(int)resp.StatusCode}: {body.Trim()}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("message", out var msg) ||
            !msg.TryGetProperty("content", out var textEl))
        {
            throw new InvalidOperationException($"Unexpected Ollama response: {body}");
        }

        string text = textEl.GetString() ?? string.Empty;

        // Strip language header, e.g. "English<asr_text>Hello world"
        const string marker = "<asr_text>";
        int idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0) text = text[(idx + marker.Length)..];

        return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    // =======================================================================
    // 6. Progress bar + SRT export
    // =======================================================================
    private static void DrawProgressBar(int done, int total, TimeSpan elapsed, int currentMs)
    {
        double pct = total == 0 ? 1.0 : Math.Clamp((double)done / total, 0.0, 1.0);

        const int barWidth = 28;
        int filled = (int)Math.Round(pct * barWidth);
        string bar = new string('#', filled) + new string('-', barWidth - filled);

        TimeSpan eta = pct > 0.0001
            ? TimeSpan.FromSeconds(Math.Max(0, elapsed.TotalSeconds / pct - elapsed.TotalSeconds))
            : TimeSpan.Zero;

        string pctStr = (pct * 100).ToString("0.0", CultureInfo.InvariantCulture);
        string line = $"  [{bar}] {pctStr,5}%  ETA {FormatHms(eta)}  at {FormatHms(TimeSpan.FromMilliseconds(currentMs))}";

        if (line.Length > _lastProgressLen) _lastProgressLen = line.Length;

        Console.Write('\r');
        Console.Write(line.PadRight(_lastProgressLen));
    }

    private static void WriteSrt(string path, List<Segment> segments)
    {
        var sb = new StringBuilder();
        int index = 1;

        foreach (Segment seg in segments)
        {
            if (string.IsNullOrWhiteSpace(seg.Text)) continue;

            sb.Append(index++.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(FormatSrtTime(seg.StartMs))
              .Append(" --> ")
              .Append(FormatSrtTime(seg.EndMs))
              .Append('\n');
            sb.Append(seg.Text).Append("\n\n");
        }

        // UTF-8 without BOM (some players show a stray glyph with BOM).
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string FormatSrtTime(int totalMs)
    {
        if (totalMs < 0) totalMs = 0;
        int h = totalMs / 3_600_000; totalMs %= 3_600_000;
        int m = totalMs / 60_000;    totalMs %= 60_000;
        int s = totalMs / 1_000;     totalMs %= 1_000;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00},{totalMs:000}");
    }

    private static string FormatHms(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int h = (int)t.TotalHours;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{t.Minutes:00}:{t.Seconds:00}");
    }

    // =======================================================================
    // Data records
    // =======================================================================
    private sealed record Chunk(int StartMs, int EndMs, byte[] Pcm);
    private sealed record Segment(int StartMs, int EndMs, string Text);
}
```

## Usage

```bash
# 1. Pull a Qwen3-ASR GGUF model into Ollama
ollama pull frozenlab/qwen3-asr:1.7b     # accuracy
# ollama pull frozenlab/qwen3-asr:0.6b   # speed

# 2. Make sure ffmpeg is on PATH
ffmpeg -version

# 3. Build & run
dotnet run --project Qwen3AsrOllama.csproj -- ./recordings
# or a single file
dotnet run --project Qwen3AsrOllama.csproj -- ./lecture.mp4 --model frozenlab/qwen3-asr:0.6b
```

## Behaviour notes

- **Discovery** scans the target directory (non-recursive) for the eight supported extensions, or accepts a single file directly.
- **Decoding** shells out to `ffmpeg -v error -i <input> -ar 16000 -ac 1 -f s16le pipe:1` and streams the raw PCM into a `MemoryStream` — no OS-specific codecs, no NuGet.
- **Chunking** scans backwards in 50 ms RMS windows from the 55 s ceiling down to a 15 s floor, splitting on the quietest window so words aren't cut in half. The final chunk absorbs the tail.
- **WAV encoding** prepends the standard 44-byte RIFF header and Base64-encodes the slice fully in memory.
- **Inference** posts `{ model, stream: false, messages: [{ role: "user", content: "", images: [<base64>] }] }` to `/api/chat` and strips everything up to and including `<asr_text>` from the reply.
- **Progress** renders an in-place bar with percentage, ETA and the current audio timestamp; it is redrawn after each chunk.
- **Output** writes `<source>.srt` next to each input file, UTF-8 without BOM, skipping empty transcripts and merging the chunk timestamps into a clean SRT timeline.