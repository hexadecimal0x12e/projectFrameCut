using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.VideoMakeEngine;
using projectFrameCut.Shared;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Unicode;
using static projectFrameCut.Services.FontHelper;
using Color = SixLabors.ImageSharp.Color;
using Font = SixLabors.Fonts.Font;
using HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment;
using PointF = SixLabors.ImageSharp.PointF;
using VerticalAlignment = SixLabors.Fonts.VerticalAlignment;

namespace projectFrameCut.Services
{
    public static class FontHelper
    {

        public static double MeasureTextLength(string text, float fontSize = 14f)
        {
            try
            {
                Font font = SystemFonts.CreateFont(SystemFonts.Families.First().Name, fontSize);
                FontRectangle rect = TextMeasurer.MeasureSize(text, new TextOptions(font));
                return rect.Width > 0 ? rect.Width : 100;
            }
            catch
            {
                return text.Length * fontSize * 0.6 + 50;
            }
        }


        public static Shared.IPicture GenerateFontThumbnail(string fontPath)
        {
            if (string.IsNullOrEmpty(fontPath) || !File.Exists(fontPath))
            {
                return Picture8bpp.GenerateSolidColor(640, 480, 255, 255, 255, null);
            }

            try
            {
                FontCollection collection = new FontCollection();
                FontFamily family = collection.Add(fontPath);
                Image<Rgba64> canvas = new(640, 480);
                canvas.Mutate((ctx) =>
                {
                    ctx.Fill(Color.White);
                    TextLanguage lang = DetectPrimaryLanguage(family);
                    string sampleText = GetSampleText(lang);
                    Font font = family.CreateFont(72, FontStyle.Regular);

                    ctx.DrawText(sampleText, font, Color.Black, new PointF(10, 240));
                });
                return new Picture8bpp(canvas);

            }
            catch
            {
                return Picture8bpp.GenerateSolidColor(640, 480, 255, 255, 255, null);
            }
        }


        private static string GetSampleText(TextLanguage lang)
        {
            return lang switch
            {
                TextLanguage.Chinese => "你好，世界！",
                TextLanguage.Japanese => "こんにちは、世界！",
                TextLanguage.Korean => "안녕하세요, 세계!",
                TextLanguage.Russian => "Привет, мир!",
                TextLanguage.Thai => "สวัสดี ชาวโลก!",
                TextLanguage.Arabic => "مرحبا بالعالم!",
                TextLanguage.English => "Hello, world!",
                _ => "Hello, world!",
            };
        }
        public enum TextLanguage
        {
            Unknown,
            English,
            Chinese,
            Japanese,
            Korean,
            Russian,
            Thai,
            Arabic
        }


        private static TextLanguage DetectPrimaryLanguage(FontFamily family)
        {
            TextLanguage result = TextLanguage.Unknown;
            if (family.Culture.ThreeLetterISOLanguageName == "ivl")
            {
                result = family.Name.ToLowerInvariant() switch
                {
                    string name when name.Contains("ja") || name.Contains("jp") => TextLanguage.Japanese,
                    string name when name.Contains("kr") || name.Contains("ko") => TextLanguage.Korean,
                    string name when name.Contains("ru") => TextLanguage.Russian,
                    string name when name.Contains("th") => TextLanguage.Thai,
                    string name when name.Contains("ar") => TextLanguage.Arabic,
                    string name when name.Contains("zh") || name.Contains("sc") || name.Contains("tc") => TextLanguage.Chinese,
                    _ => TextLanguage.English,
                };
            }
            else
            {
                result = family.Culture.Name.StartsWith("ja") ? TextLanguage.Japanese :
                                      family.Culture.Name.StartsWith("ko") ? TextLanguage.Korean :
                                      family.Culture.Name.StartsWith("ru") ? TextLanguage.Russian :
                                      family.Culture.Name.StartsWith("th") ? TextLanguage.Thai :
                                      family.Culture.Name.StartsWith("ar") ? TextLanguage.Arabic :
                                      family.Culture.Name.StartsWith("zh") ? TextLanguage.Chinese :
                                      TextLanguage.English;
            }


            Log($"Font {family.Name}: consider as {result}.");

            return result;
        }
    }


}
