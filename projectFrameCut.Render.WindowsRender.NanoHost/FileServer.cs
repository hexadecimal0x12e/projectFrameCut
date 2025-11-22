namespace projectFrameCut.Render.WindowsRender.NanoHost
{
    public static class FileServer
    {
        public static string ContentRoot = AppContext.BaseDirectory;

        public static IResult ReadThumb(string fHash)
        {
            try
            {
                var path = Path.Combine(ContentRoot, $"projectFrameCut_Render_{fHash}.jpg");
                if (!System.IO.File.Exists(path))
                {
                    return Results.NotFound("File not found.");
                }
                var bytes = System.IO.File.ReadAllBytes(path);
                return Results.File(bytes, "image/jpeg", enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        }
    }
}
