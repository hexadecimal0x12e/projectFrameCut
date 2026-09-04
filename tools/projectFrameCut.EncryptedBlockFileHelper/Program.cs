using System.Globalization;
using System.Security.Cryptography;
using projectFrameCut.Shared;

return EncryptedBlockFileCli.Run(args);

internal static class EncryptedBlockFileCli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "generate-key" => GenerateKey(args.AsSpan(1)),
                "encrypt" => ProcessFile(args.AsSpan(1), encrypt: true),
                "decrypt" => ProcessFile(args.AsSpan(1), encrypt: false),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or
            OverflowException or InvalidDataException or IOException or UnauthorizedAccessException or
            CryptographicException or NotSupportedException)
        {
            Console.Error.WriteLine($"Operation failed: {exception.Message}");
            Logger.Log(exception, "processing an encrypted file", nameof(EncryptedBlockFileCli));
            return 1;
        }
    }

    private static int GenerateKey(ReadOnlySpan<string> args)
    {
        foreach (string argument in args)
        {
            if (argument is "-h" or "--help")
            {
                PrintGenerateKeyUsage();
                return 0;
            }

            throw new ArgumentException($"Unknown option '{argument}'.");
        }

        byte[] key = EncryptedStreamCrypto.GenerateKey();
        try
        {
            Console.WriteLine(Convert.ToBase64String(key));
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static int ProcessFile(ReadOnlySpan<string> args, bool encrypt)
    {
        FileCommandOptions? options = ParseFileOptions(args, encrypt);
        if (options is null) return 0;

        byte[] key = ResolveKey(options);
        try
        {
            string inputPath = Path.GetFullPath(options.InputPath);
            string outputPath = Path.GetFullPath(options.OutputPath);
            ValidatePaths(inputPath, outputPath, options.Force);

            if (encrypt)
            {
                EncryptedStreamCrypto.EncryptFile(
                    inputPath,
                    outputPath,
                    key,
                    options.BlockSize,
                    options.Force);
            }
            else
            {
                DecryptFile(inputPath, outputPath, key, options.Force);
            }

            Console.WriteLine($"{(encrypt ? "Encrypted" : "Decrypted")} '{inputPath}' to '{outputPath}'.");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static FileCommandOptions? ParseFileOptions(ReadOnlySpan<string> args, bool encrypt)
    {
        string? input = null;
        string? output = null;
        string? key = null;
        string? keyEnvironmentVariable = null;
        var keyFromStandardInput = false;
        var force = false;
        var blockSize = EncryptedStreamCrypto.DefaultBlockSize;

        for (var index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--input":
                    input = ReadValue(args, ref index, argument);
                    break;
                case "--output":
                    output = ReadValue(args, ref index, argument);
                    break;
                case "--key":
                    key = ReadValue(args, ref index, argument);
                    break;
                case "--key-env":
                    keyEnvironmentVariable = ReadValue(args, ref index, argument);
                    break;
                case "--key-stdin":
                    keyFromStandardInput = true;
                    break;
                case "--block-size":
                    if (!encrypt)
                        throw new ArgumentException("--block-size is only valid for encrypt.");
                    string blockSizeText = ReadValue(args, ref index, argument);
                    if (!int.TryParse(blockSizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out blockSize))
                        throw new ArgumentException($"Invalid block size '{blockSizeText}'.");
                    break;
                case "--force":
                    force = true;
                    break;
                case "-h":
                case "--help":
                    PrintFileUsage(encrypt);
                    return null;
                default:
                    throw new ArgumentException($"Unknown option '{argument}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--input and --output are required.");

        var keySourceCount = (key is not null ? 1 : 0) +
                             (keyEnvironmentVariable is not null ? 1 : 0) +
                             (keyFromStandardInput ? 1 : 0);
        if (keySourceCount != 1)
            throw new ArgumentException("Specify exactly one of --key, --key-env, or --key-stdin.");

        return new FileCommandOptions(input, output, key, keyEnvironmentVariable,
            keyFromStandardInput, blockSize, force);
    }

    private static byte[] ResolveKey(FileCommandOptions options)
    {
        string keyText;
        if (options.Key is not null)
        {
            keyText = options.Key;
        }
        else if (options.KeyEnvironmentVariable is not null)
        {
            keyText = Environment.GetEnvironmentVariable(options.KeyEnvironmentVariable)
                ?? throw new ArgumentException(
                    $"Environment variable '{options.KeyEnvironmentVariable}' is not set.");
        }
        else
        {
            keyText = Console.In.ReadLine() ??
                throw new ArgumentException("No Base64 key was supplied on standard input.");
        }

        if (string.IsNullOrWhiteSpace(keyText))
            throw new ArgumentException("The Base64 key cannot be empty.");

        return EncryptedStreamCrypto.KeyFromBase64(keyText.Trim());
    }

    private static void ValidatePaths(string inputPath, string outputPath, bool force)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("The input file was not found.", inputPath);
        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The input and output paths must be different.");
        if (!force && File.Exists(outputPath))
            throw new IOException($"The output file already exists: '{outputPath}'. Use --force to replace it.");
    }

    private static void DecryptFile(string inputPath, string outputPath, byte[] key, bool force)
    {
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        string temporaryPath = Path.Combine(
            outputDirectory ?? Directory.GetCurrentDirectory(),
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var source = EncryptedStreamCrypto.OpenRead(inputPath, key))
            using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            {
                source.CopyTo(destination, 1024 * 1024);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, outputPath, force);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string ReadValue(ReadOnlySpan<string> args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"Option '{option}' requires a value.");

        return args[index];
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("projectFrameCut Encrypted Block File Helper");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  generate-key");
        Console.WriteLine("  encrypt --input <file> --output <file> <key-source> [--block-size <bytes>] [--force]");
        Console.WriteLine("  decrypt --input <file> --output <file> <key-source> [--force]");
        Console.WriteLine();
        Console.WriteLine("Key source (choose exactly one):");
        Console.WriteLine("  --key <base64>       Use a Base64 encoded AES key.");
        Console.WriteLine("  --key-env <NAME>     Read the Base64 key from an environment variable.");
        Console.WriteLine("  --key-stdin          Read one Base64 key line from standard input.");
        Console.WriteLine();
        Console.WriteLine("Use --help after a command for command-specific help.");
    }

    private static void PrintGenerateKeyUsage() =>
        Console.WriteLine("Usage: generate-key");

    private static void PrintFileUsage(bool encrypt)
    {
        Console.WriteLine($"Usage: {(encrypt ? "encrypt" : "decrypt")} --input <file> --output <file> <key-source>" +
            (encrypt ? " [--block-size <bytes>]" : "") + " [--force]");
        Console.WriteLine();
        Console.WriteLine("Key source: --key <base64> | --key-env <NAME> | --key-stdin");
        if (encrypt)
            Console.WriteLine($"Default block size: {EncryptedStreamCrypto.DefaultBlockSize} bytes.");
    }

    private sealed record FileCommandOptions(
        string InputPath,
        string OutputPath,
        string? Key,
        string? KeyEnvironmentVariable,
        bool KeyFromStandardInput,
        int BlockSize,
        bool Force);
}
