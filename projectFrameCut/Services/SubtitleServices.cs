using projectFrameCut.Render.Subtitles;
using System.Text;

namespace projectFrameCut.Services;

public static class SubtitleServices
{
    public static async Task<(SubtitleImportResult Result, string Name)?> PickAsync(Page page)
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Localized.Subtitle_Import });
        if (file is null) return null;
        if (!SubtitleImporter.Supports(file.FileName)) throw new NotSupportedException(Localized.Subtitle_Formats);
        var result = await ReadAsync(file.OpenReadAsync, Path.GetExtension(file.FileName), page);
        return result is null ? null : (result, Path.GetFileNameWithoutExtension(file.FileName));
    }

    public static Task<SubtitleImportResult?> ReadAsync(string path, Page page) =>
        ReadAsync(() => Task.FromResult<Stream>(File.OpenRead(path)), Path.GetExtension(path), page);

    private static async Task<SubtitleImportResult?> ReadAsync(Func<Task<Stream>> open, string format, Page page)
    {
        Encoding encoding = new UTF8Encoding(false, true);
        while (true)
        {
            try
            {
                using var stream = await open();
                using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
                string text = await reader.ReadToEndAsync();
                var result = await Task.Run(() => SubtitleImporter.Parse(text, format));
                if (result.Warnings.Count > 0)
                {
                    foreach (var warning in result.Warnings) Log(warning, "warn");
                    await page.DisplayAlertAsync(Localized.Subtitle_Title, Localized.Subtitle_ImportWarnings, Localized._OK);
                }
                return result;
            }
            catch (DecoderFallbackException ex)
            {
                Log(ex, "Decode subtitle file");
                string? selected = await page.DisplayActionSheetAsync(Localized.Subtitle_Encoding, Localized._Cancel, null, "UTF-8", "GB18030", "Windows-1252", "UTF-16LE", "UTF-16BE");
                if (selected is null || selected == Localized._Cancel) return null;
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding(selected switch { "UTF-16LE" => "utf-16", "UTF-16BE" => "unicodeFFFE", _ => selected }, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
        }
    }
}
