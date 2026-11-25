# Qwen3-ASR Ollama Batch Transcriber

A zero-NuGet C# console application that batches audio and video files through a local [Ollama](https://ollama.com/) Qwen3-ASR model and writes timestamped SRT subtitles.

## Features

- Transcribes a single file or every supported file in a directory
- Writes a UTF-8 `.srt` file next to each source file
- Splits long recordings at quiet 50 ms windows
- Uses in-memory WAV encoding and the Ollama `/api/chat` API
- Displays chunk progress, elapsed time, and ETA
- Supports case-insensitive file extensions

Supported formats: `.m4a`, `.mp3`, `.wav`, `.mp4`, `.ogg`, `.flac`, `.aac`, and `.wma`

## Requirements

- .NET 8 SDK or newer
- [FFmpeg](https://ffmpeg.org/) available on `PATH`
- [Ollama](https://ollama.com/) running locally
- A Qwen3-ASR Ollama model

Pull the default model:

```powershell
ollama pull frozenlab/qwen3-asr:1.7b
```

Install FFmpeg with Windows Package Manager:

```powershell
winget install --id Gyan.FFmpeg --exact
```

Verify both tools:

```powershell
dotnet --version
ffmpeg -version
ollama list
```

## Build

```powershell
dotnet restore .\V2TConvertor.sln
dotnet build .\V2TConvertor.sln --configuration Release
```

## Usage

Transcribe a directory:

```powershell
dotnet run --project .\Qwen3AsrOllama.csproj -- .\recordings
```

Transcribe one file:

```powershell
dotnet run --project .\Qwen3AsrOllama.csproj -- .\lecture.mp4
```

Use an alternate model:

```powershell
dotnet run --project .\Qwen3AsrOllama.csproj -- .\meeting.m4a --model frozenlab/qwen3-asr:0.6b
```

Use an Ollama endpoint other than `http://localhost:11434`:

```powershell
dotnet run --project .\Qwen3AsrOllama.csproj -- .\voice.wav --endpoint http://localhost:11434
```

## Command-Line Syntax

```text
qwen3-asr <file-or-directory> [--model <name>] [--endpoint <url>]
```

Options:

| Option | Default | Description |
| --- | --- | --- |
| `--model` | `frozenlab/qwen3-asr:1.7b` | Ollama ASR model name |
| `--endpoint` | `http://localhost:11434` | Ollama server URL |

Directory discovery is non-recursive and processes supported files in alphabetical order. Each source file receives an SRT file with the same base name.

## How It Works

1. FFmpeg decodes the input to mono, 16 kHz, signed 16-bit PCM.
2. PCM is split into chunks of up to 55 seconds.
3. The 15-second to 55-second range is scanned for the quietest 50 ms window.
4. Each chunk becomes an in-memory WAV and is Base64-encoded.
5. Ollama `/api/chat` receives the image-style audio payload.
6. `<asr_text>` responses are converted to SRT segments.

## Tests

The test project downloads a public CC0 WAV voice fixture, converts it to M4A, then tests both WAV and M4A through FFmpeg and Ollama:

```powershell
dotnet test .\V2TConvertor.sln --configuration Release
```

The integration tests require:

- Internet access for the online fixture
- FFmpeg
- Ollama running at `http://localhost:11434`
- `frozenlab/qwen3-asr:1.7b` installed

## Troubleshooting

### FFmpeg was not found

Install FFmpeg and reopen the terminal so the updated `PATH` is available. The app also checks the standard WinGet FFmpeg alias on Windows.

### Ollama connection fails

Confirm Ollama is running and inspect available models:

```powershell
ollama list
Invoke-RestMethod http://localhost:11434/api/tags
```

### The model cannot be found

Pull it explicitly:

```powershell
ollama pull frozenlab/qwen3-asr:1.7b
```
