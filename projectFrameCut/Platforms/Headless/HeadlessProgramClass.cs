namespace projectFrameCut;

public static class HeadlessProgramClass
{
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault()?.Equals("gui", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.Error.WriteLine("ERROR: 'gui' command is not available in headless client.");
            return 65535;
        }
        try
        {
            MauiProgram.InitializeHeadless(args);
            return CLIProgram.CLIMain(args);
        }
        catch (Exception ex)
        {
            Log(ex, "initialize headless client");
            Console.Error.WriteLine($"Headless client failed: {ex.Message}");
            return 1;
        }
    }
}
