using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Version2.Capture;

if (args.Length != 2 || args[0] != "--fixture" || !File.Exists(Path.Combine(args[1], "fixture-only")))
    throw new InvalidOperationException("Use --fixture with the isolated capture_fixture directory. Live ADE is never probed.");
var directory = Path.GetFullPath(args[1]);
using var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "services", CaptureClient.Service + ".json")));
var endpoint = new CaptureEndpoint(record.RootElement.GetProperty("url").GetString()!, record.RootElement.GetProperty("token").GetString()!, record.RootElement.GetProperty("instanceId").GetString()!);
var client = new CaptureClient(Path.Combine(directory, "outbox"), resolve: () => endpoint);
var targets = await client.DestinationsAsync();
if (targets.Count != 1) throw new InvalidOperationException("Fixture inventory mismatch.");
var submission = CaptureClient.Text(targets[0], "Preserve 42, C++ and the last sentence.");
if (!(await client.StageAsync(submission)).Accepted || !(await client.StageAsync(submission)).Accepted) throw new InvalidOperationException("Text staging/replay failed.");
using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "inbox", submission.OperationId, "draft.json")));
if (saved.RootElement.GetProperty("text").GetString() != submission.Text) throw new InvalidOperationException("Transcription changed.");
var pixels = new byte[8 * 8 * 4]; Array.Fill(pixels, (byte)180);
var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 32); bitmap.Freeze();
var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
using var png = new MemoryStream(); encoder.Save(png); var bytes = png.ToArray();
var screenshot = CaptureClient.Screenshot(targets[0], "final-fixture.png", bytes);
if (!(await client.StageAsync(screenshot)).Accepted || !(await client.StageAsync(screenshot)).Accepted) throw new InvalidOperationException("Image staging/replay failed.");
var owned = await File.ReadAllBytesAsync(Path.Combine(directory, "inbox", screenshot.OperationId, "capture.png"));
if (!SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(owned))) throw new InvalidOperationException("Receiver pixels changed.");
if (Directory.GetDirectories(Path.Combine(directory, "inbox")).Length != 2 || client.Pending().Count != 0) throw new InvalidOperationException("Replay duplicated a draft or left an outbox item.");
Console.WriteLine(JsonSerializer.Serialize(new { textPreserved = true, imageHashMatched = true, repeatedOperations = "one record each", drafts = 2, pending = 0, liveDesktopTouched = false }));
