using System.Security.Cryptography;
using System.Text;

namespace projectFrameCut.Shared;

public class AsymmetricCrypto //some AI generated stuff
{
    /// <summary>
    /// RSA 密钥大小（位）
    /// </summary>
    public const int KeySize = 2048;

    /// <summary>
    /// AES 密钥大小（位）
    /// </summary>
    private const int AesKeySize = 256;

    /// <summary>
    /// 加密文件头标识
    /// </summary>
    private static readonly byte[] FileHeaderMagic = Encoding.ASCII.GetBytes("pjfc_");
    
    /// <summary>
    /// 加密文件版本号
    /// </summary>
    private const byte FileVersion = 1;

    /// <summary>
    /// 生成 RSA 密钥对
    /// </summary>
    /// <returns>包含公钥和私钥的元组</returns>
    public static (string publicKey, string privateKey) GenerateKeyPair()
    {
        using var rsa = RSA.Create(KeySize);
        var publicKey = Convert.ToBase64String(rsa.ExportRSAPublicKey());
        var privateKey = Convert.ToBase64String(rsa.ExportRSAPrivateKey());
        return (publicKey, privateKey);
    }

    /// <summary>
    /// 使用公钥加密字符串
    /// </summary>
    /// <param name="plainText">待加密的明文</param>
    /// <param name="publicKey">Base64 编码的公钥</param>
    /// <returns>Base64 编码的密文</returns>
    public static string Encrypt(string plainText, string publicKey)
    {
        if (string.IsNullOrEmpty(plainText))
            throw new ArgumentNullException(nameof(plainText));
        if (string.IsNullOrEmpty(publicKey))
            throw new ArgumentNullException(nameof(publicKey));

        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKey), out _);
        
        var dataToEncrypt = Encoding.UTF8.GetBytes(plainText);
        var encryptedData = rsa.Encrypt(dataToEncrypt, RSAEncryptionPadding.OaepSHA256);
        
        return Convert.ToBase64String(encryptedData);
    }

    /// <summary>
    /// 使用私钥解密字符串
    /// </summary>
    /// <param name="cipherText">Base64 编码的密文</param>
    /// <param name="privateKey">Base64 编码的私钥</param>
    /// <returns>解密后的明文</returns>
    public static string Decrypt(string cipherText, string privateKey)
    {
        if (string.IsNullOrEmpty(cipherText))
            throw new ArgumentNullException(nameof(cipherText));
        if (string.IsNullOrEmpty(privateKey))
            throw new ArgumentNullException(nameof(privateKey));

        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(privateKey), out _);
        
        var dataToDecrypt = Convert.FromBase64String(cipherText);
        var decryptedData = rsa.Decrypt(dataToDecrypt, RSAEncryptionPadding.OaepSHA256);
        
        return Encoding.UTF8.GetString(decryptedData);
    }

    /// <summary>
    /// 使用私钥加密字符串（用于验证真实性，与公钥解密配合使用）
    /// </summary>
    /// <param name="plainText">待加密的明文</param>
    /// <param name="privateKey">Base64 编码的私钥</param>
    /// <returns>Base64 编码的密文</returns>
    public static string EncryptWithPrivateKey(string plainText, string privateKey)
    {
        if (string.IsNullOrEmpty(plainText))
            throw new ArgumentNullException(nameof(plainText));
        if (string.IsNullOrEmpty(privateKey))
            throw new ArgumentNullException(nameof(privateKey));

        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(privateKey), out _);
        
        var dataToEncrypt = Encoding.UTF8.GetBytes(plainText);
        var encryptedData = rsa.Encrypt(dataToEncrypt, RSAEncryptionPadding.OaepSHA256);
        
        return Convert.ToBase64String(encryptedData);
    }

    /// <summary>
    /// 使用公钥解密字符串（解密由私钥加密的数据）
    /// </summary>
    /// <param name="cipherText">Base64 编码的密文</param>
    /// <param name="publicKey">Base64 编码的公钥</param>
    /// <returns>解密后的明文</returns>
    public static string DecryptWithPublicKey(string cipherText, string publicKey)
    {
        if (string.IsNullOrEmpty(cipherText))
            throw new ArgumentNullException(nameof(cipherText));
        if (string.IsNullOrEmpty(publicKey))
            throw new ArgumentNullException(nameof(publicKey));

        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKey), out _);
        
        var dataToDecrypt = Convert.FromBase64String(cipherText);
        var decryptedData = rsa.Decrypt(dataToDecrypt, RSAEncryptionPadding.OaepSHA256);
        
        return Encoding.UTF8.GetString(decryptedData);
    }

    /// <summary>
    /// 使用公钥加密字节数组
    /// </summary>
    /// <param name="data">待加密的数据</param>
    /// <param name="publicKey">Base64 编码的公钥</param>
    /// <returns>加密后的字节数组</returns>
    public static byte[] EncryptBytes(byte[] data, string publicKey)
    {
        if (data == null || data.Length == 0)
            throw new ArgumentNullException(nameof(data));
        if (string.IsNullOrEmpty(publicKey))
            throw new ArgumentNullException(nameof(publicKey));

        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKey), out _);
        
        return rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256);
    }

    /// <summary>
    /// 使用私钥解密字节数组
    /// </summary>
    /// <param name="encryptedData">加密的数据</param>
    /// <param name="privateKey">Base64 编码的私钥</param>
    /// <returns>解密后的字节数组</returns>
    public static byte[] DecryptBytes(byte[] encryptedData, string privateKey)
    {
        if (encryptedData == null || encryptedData.Length == 0)
            throw new ArgumentNullException(nameof(encryptedData));
        if (string.IsNullOrEmpty(privateKey))
            throw new ArgumentNullException(nameof(privateKey));

        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(privateKey), out _);
        
        return rsa.Decrypt(encryptedData, RSAEncryptionPadding.OaepSHA256);
    }

    /// <summary>
    /// 使用私钥加密字节数组（用于验证真实性）
    /// </summary>
    /// <param name="data">待加密的数据</param>
    /// <param name="privateKey">Base64 编码的私钥</param>
    /// <returns>加密后的字节数组</returns>
    public static byte[] EncryptBytesWithPrivateKey(byte[] data, string privateKey)
    {
        if (data == null || data.Length == 0)
            throw new ArgumentNullException(nameof(data));
        if (string.IsNullOrEmpty(privateKey))
            throw new ArgumentNullException(nameof(privateKey));

        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(privateKey), out _);
        
        return rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256);
    }

    /// <summary>
    /// 使用公钥解密字节数组（解密由私钥加密的数据）
    /// </summary>
    /// <param name="encryptedData">加密的数据</param>
    /// <param name="publicKey">Base64 编码的公钥</param>
    /// <returns>解密后的字节数组</returns>
    public static byte[] DecryptBytesWithPublicKey(byte[] encryptedData, string publicKey)
    {
        if (encryptedData == null || encryptedData.Length == 0)
            throw new ArgumentNullException(nameof(encryptedData));
        if (string.IsNullOrEmpty(publicKey))
            throw new ArgumentNullException(nameof(publicKey));

        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKey), out _);
        
        return rsa.Decrypt(encryptedData, RSAEncryptionPadding.OaepSHA256);
    }

    /// <summary>
    /// 使用私钥对数据进行签名
    /// </summary>
    /// <param name="data">待签名的数据</param>
    /// <param name="privateKey">Base64 编码的私钥</param>
    /// <returns>Base64 编码的签名</returns>
    public static string Sign(string data, string privateKey)
    {
        if (string.IsNullOrEmpty(data))
            throw new ArgumentNullException(nameof(data));
        if (string.IsNullOrEmpty(privateKey))
            throw new ArgumentNullException(nameof(privateKey));

        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(privateKey), out _);
        
        var dataToSign = Encoding.UTF8.GetBytes(data);
        var signature = rsa.SignData(dataToSign, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// 使用公钥验证签名
    /// </summary>
    /// <param name="data">原始数据</param>
    /// <param name="signature">Base64 编码的签名</param>
    /// <param name="publicKey">Base64 编码的公钥</param>
    /// <returns>签名是否有效</returns>
    public static bool VerifySignature(string data, string signature, string publicKey)
    {
        if (string.IsNullOrEmpty(data))
            throw new ArgumentNullException(nameof(data));
        if (string.IsNullOrEmpty(signature))
            throw new ArgumentNullException(nameof(signature));
        if (string.IsNullOrEmpty(publicKey))
            throw new ArgumentNullException(nameof(publicKey));

        try
        {
            using var rsa = RSA.Create(KeySize);
            rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKey), out _);
            
            var dataToVerify = Encoding.UTF8.GetBytes(data);
            var signatureBytes = Convert.FromBase64String(signature);
            
            return rsa.VerifyData(dataToVerify, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从 PEM 格式导入公钥
    /// </summary>
    /// <param name="pem">PEM 格式的公钥字符串</param>
    /// <returns>Base64 编码的公钥</returns>
    public static string ImportPublicKeyFromPem(string pem)
    {
        if (string.IsNullOrEmpty(pem))
            throw new ArgumentNullException(nameof(pem));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return Convert.ToBase64String(rsa.ExportRSAPublicKey());
    }

    /// <summary>
    /// 从 PEM 格式导入私钥
    /// </summary>
    /// <param name="pem">PEM 格式的私钥字符串</param>
    /// <returns>Base64 编码的私钥</returns>
    public static string ImportPrivateKeyFromPem(string pem)
    {
        if (string.IsNullOrEmpty(pem))
            throw new ArgumentNullException(nameof(pem));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return Convert.ToBase64String(rsa.ExportRSAPrivateKey());
    }

    /// <summary>
    /// 导出公钥为 PEM 格式
    /// </summary>
    /// <param name="publicKey">Base64 编码的公钥</param>
    /// <returns>PEM 格式的公钥字符串</returns>
    public static string ExportPublicKeyToPem(string publicKey)
    {
        if (string.IsNullOrEmpty(publicKey))
            throw new ArgumentNullException(nameof(publicKey));

        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKey), out _);
        return rsa.ExportRSAPublicKeyPem();
    }

    /// <summary>
    /// 导出私钥为 PEM 格式
    /// </summary>
    /// <param name="privateKey">Base64 编码的私钥</param>
    /// <returns>PEM 格式的私钥字符串</returns>
    public static string ExportPrivateKeyToPem(string privateKey)
    {
        if (string.IsNullOrEmpty(privateKey))
            throw new ArgumentNullException(nameof(privateKey));

        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(privateKey), out _);
        return rsa.ExportRSAPrivateKeyPem();
    }


    #region Base64 与 16进制 转换

    /// <summary>
    /// 将 Base64 编码的密钥转换为 16 进制字符串
    /// </summary>
    /// <param name="base64Key">Base64 编码的密钥</param>
    /// <returns>16 进制字符串（小写）</returns>
    public static string Base64ToHex(string base64Key)
    {
        if (string.IsNullOrEmpty(base64Key))
            throw new ArgumentNullException(nameof(base64Key));

        var bytes = Convert.FromBase64String(base64Key);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// 将 16 进制字符串转换为 Base64 编码的密钥
    /// </summary>
    /// <param name="hexKey">16 进制字符串</param>
    /// <returns>Base64 编码的密钥</returns>
    public static string HexToBase64(string hexKey)
    {
        if (string.IsNullOrEmpty(hexKey))
            throw new ArgumentNullException(nameof(hexKey));

        var bytes = Convert.FromHexString(hexKey);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// 生成 RSA 密钥对（16 进制格式）
    /// </summary>
    /// <returns>包含公钥和私钥的元组（16进制字符串）</returns>
    public static (string publicKeyHex, string privateKeyHex) GenerateKeyPairHex()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        return (Base64ToHex(publicKey), Base64ToHex(privateKey));
    }

    /// <summary>
    /// 使用私钥加密字符串（16 进制密钥）
    /// </summary>
    /// <param name="plainText">待加密的明文</param>
    /// <param name="privateKeyHex">16 进制编码的私钥</param>
    /// <returns>Base64 编码的密文</returns>
    public static string EncryptWithPrivateKeyHex(string plainText, string privateKeyHex)
    {
        var privateKey = HexToBase64(privateKeyHex);
        return EncryptWithPrivateKey(plainText, privateKey);
    }

    /// <summary>
    /// 使用公钥解密字符串（16 进制密钥）
    /// </summary>
    /// <param name="cipherText">Base64 编码的密文</param>
    /// <param name="publicKeyHex">16 进制编码的公钥</param>
    /// <returns>解密后的明文</returns>
    public static string DecryptWithPublicKeyHex(string cipherText, string publicKeyHex)
    {
        var publicKey = HexToBase64(publicKeyHex);
        return DecryptWithPublicKey(cipherText, publicKey);
    }

    #endregion

    #region 文件流加密解密（混合加密：RSA + AES）

    /// <summary>
    /// 使用公钥加密文件流（混合加密）
    /// </summary>
    /// <param name="inputStream">输入流</param>
    /// <param name="outputStream">输出流</param>
    /// <param name="publicKey">Base64 编码的公钥</param>
    /// <param name="bufferSize">缓冲区大小（默认 64KB）</param>
    /// <param name="progress">进度回调（参数：已处理字节数，总字节数）</param>
    public static void EncryptStream(Stream inputStream, Stream outputStream, string publicKey, 
        int bufferSize = 65536, IProgress<(long processed, long total)>? progress = null)
    {
        if (inputStream == null)
            throw new ArgumentNullException(nameof(inputStream));
        if (outputStream == null)
            throw new ArgumentNullException(nameof(outputStream));
        if (string.IsNullOrEmpty(publicKey))
            throw new ArgumentNullException(nameof(publicKey));
        if (!inputStream.CanRead)
            throw new ArgumentException("输入流不可读", nameof(inputStream));
        if (!outputStream.CanWrite)
            throw new ArgumentException("输出流不可写", nameof(outputStream));

        // 生成随机 AES 密钥和 IV
        using var aes = Aes.Create();
        aes.KeySize = AesKeySize;
        aes.GenerateKey();
        aes.GenerateIV();

        // 使用 RSA 加密 AES 密钥和 IV
        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKey), out _);
        
        var encryptedKey = rsa.Encrypt(aes.Key, RSAEncryptionPadding.OaepSHA256);
        var encryptedIV = rsa.Encrypt(aes.IV, RSAEncryptionPadding.OaepSHA256);

        // 写入文件头
        // 格式: [Magic(5字节)] [Version(1字节)] [KeyLength(4字节)] [EncryptedKey] [IVLength(4字节)] [EncryptedIV] [Data]
        outputStream.Write(FileHeaderMagic, 0, FileHeaderMagic.Length);
        outputStream.WriteByte(FileVersion);
        outputStream.Write(BitConverter.GetBytes(encryptedKey.Length), 0, 4);
        outputStream.Write(encryptedKey, 0, encryptedKey.Length);
        outputStream.Write(BitConverter.GetBytes(encryptedIV.Length), 0, 4);
        outputStream.Write(encryptedIV, 0, encryptedIV.Length);

        // 使用 AES 加密数据流
        using var cryptoStream = new CryptoStream(outputStream, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
        
        var buffer = new byte[bufferSize];
        long totalBytes = inputStream.CanSeek ? inputStream.Length : 0;
        long processedBytes = 0;
        int bytesRead;

        while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cryptoStream.Write(buffer, 0, bytesRead);
            processedBytes += bytesRead;
            progress?.Report((processedBytes, totalBytes));
        }

        cryptoStream.FlushFinalBlock();
    }

    /// <summary>
    /// 使用私钥解密文件流（混合解密）
    /// </summary>
    /// <param name="inputStream">输入流</param>
    /// <param name="outputStream">输出流</param>
    /// <param name="privateKey">Base64 编码的私钥</param>
    /// <param name="bufferSize">缓冲区大小（默认 64KB）</param>
    /// <param name="progress">进度回调（参数：已处理字节数，总字节数）</param>
    public static void DecryptStream(Stream inputStream, Stream outputStream, string privateKey,
        int bufferSize = 65536, IProgress<(long processed, long total)>? progress = null)
    {
        if (inputStream == null)
            throw new ArgumentNullException(nameof(inputStream));
        if (outputStream == null)
            throw new ArgumentNullException(nameof(outputStream));
        if (string.IsNullOrEmpty(privateKey))
            throw new ArgumentNullException(nameof(privateKey));
        if (!inputStream.CanRead)
            throw new ArgumentException("输入流不可读", nameof(inputStream));
        if (!outputStream.CanWrite)
            throw new ArgumentException("输出流不可写", nameof(outputStream));

        // 读取并验证文件头
        var magic = new byte[FileHeaderMagic.Length];
        if (inputStream.Read(magic, 0, magic.Length) != magic.Length || !magic.SequenceEqual(FileHeaderMagic))
            throw new InvalidDataException("无效的加密文件格式");

        var version = inputStream.ReadByte();
        if (version != FileVersion)
            throw new InvalidDataException($"不支持的文件版本: {version}");

        // 读取加密的 AES 密钥
        var keyLengthBytes = new byte[4];
        inputStream.Read(keyLengthBytes, 0, 4);
        var keyLength = BitConverter.ToInt32(keyLengthBytes, 0);
        var encryptedKey = new byte[keyLength];
        inputStream.Read(encryptedKey, 0, keyLength);

        // 读取加密的 AES IV
        var ivLengthBytes = new byte[4];
        inputStream.Read(ivLengthBytes, 0, 4);
        var ivLength = BitConverter.ToInt32(ivLengthBytes, 0);
        var encryptedIV = new byte[ivLength];
        inputStream.Read(encryptedIV, 0, ivLength);

        // 使用 RSA 解密 AES 密钥和 IV
        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(privateKey), out _);
        
        var aesKey = rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);
        var aesIV = rsa.Decrypt(encryptedIV, RSAEncryptionPadding.OaepSHA256);

        // 使用 AES 解密数据流
        using var aes = Aes.Create();
        aes.KeySize = AesKeySize;
        aes.Key = aesKey;
        aes.IV = aesIV;

        using var cryptoStream = new CryptoStream(inputStream, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
        
        var buffer = new byte[bufferSize];
        long totalBytes = inputStream.CanSeek ? inputStream.Length : 0;
        long processedBytes = 0;
        int bytesRead;

        while ((bytesRead = cryptoStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            outputStream.Write(buffer, 0, bytesRead);
            processedBytes += bytesRead;
            progress?.Report((processedBytes, totalBytes));
        }
    }

    /// <summary>
    /// 使用公钥加密文件（混合加密）
    /// </summary>
    /// <param name="inputFilePath">输入文件路径</param>
    /// <param name="outputFilePath">输出文件路径</param>
    /// <param name="publicKey">Base64 编码的公钥</param>
    /// <param name="progress">进度回调</param>
    public static void EncryptFile(string inputFilePath, string outputFilePath, string publicKey,
        IProgress<(long processed, long total)>? progress = null)
    {
        if (string.IsNullOrEmpty(inputFilePath))
            throw new ArgumentNullException(nameof(inputFilePath));
        if (string.IsNullOrEmpty(outputFilePath))
            throw new ArgumentNullException(nameof(outputFilePath));
        if (!File.Exists(inputFilePath))
            throw new FileNotFoundException("输入文件不存在", inputFilePath);

        using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        
        EncryptStream(inputStream, outputStream, publicKey, progress: progress);
    }

    /// <summary>
    /// 使用私钥加密文件流（混合加密，用于验证真实性）
    /// </summary>
    /// <param name="inputStream">输入流</param>
    /// <param name="outputStream">输出流</param>
    /// <param name="privateKey">Base64 编码的私钥</param>
    /// <param name="bufferSize">缓冲区大小（默认 64KB）</param>
    /// <param name="progress">进度回调（参数：已处理字节数，总字节数）</param>
    public static void EncryptStreamWithPrivateKey(Stream inputStream, Stream outputStream, string privateKey, 
        int bufferSize = 65536, IProgress<(long processed, long total)>? progress = null)
    {
        if (inputStream == null)
            throw new ArgumentNullException(nameof(inputStream));
        if (outputStream == null)
            throw new ArgumentNullException(nameof(outputStream));
        if (string.IsNullOrEmpty(privateKey))
            throw new ArgumentNullException(nameof(privateKey));
        if (!inputStream.CanRead)
            throw new ArgumentException("输入流不可读", nameof(inputStream));
        if (!outputStream.CanWrite)
            throw new ArgumentException("输出流不可写", nameof(outputStream));

        // 生成随机 AES 密钥和 IV
        using var aes = Aes.Create();
        aes.KeySize = AesKeySize;
        aes.GenerateKey();
        aes.GenerateIV();

        // 使用 RSA 加密 AES 密钥和 IV
        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(privateKey), out _);
        
        var encryptedKey = rsa.Encrypt(aes.Key, RSAEncryptionPadding.OaepSHA256);
        var encryptedIV = rsa.Encrypt(aes.IV, RSAEncryptionPadding.OaepSHA256);

        // 写入文件头
        // 格式: [Magic(5字节)] [Version(1字节)] [KeyLength(4字节)] [EncryptedKey] [IVLength(4字节)] [EncryptedIV] [Data]
        outputStream.Write(FileHeaderMagic, 0, FileHeaderMagic.Length);
        outputStream.WriteByte(FileVersion);
        outputStream.Write(BitConverter.GetBytes(encryptedKey.Length), 0, 4);
        outputStream.Write(encryptedKey, 0, encryptedKey.Length);
        outputStream.Write(BitConverter.GetBytes(encryptedIV.Length), 0, 4);
        outputStream.Write(encryptedIV, 0, encryptedIV.Length);

        // 使用 AES 加密数据流
        using var cryptoStream = new CryptoStream(outputStream, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
        
        var buffer = new byte[bufferSize];
        long totalBytes = inputStream.CanSeek ? inputStream.Length : 0;
        long processedBytes = 0;
        int bytesRead;

        while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cryptoStream.Write(buffer, 0, bytesRead);
            processedBytes += bytesRead;
            progress?.Report((processedBytes, totalBytes));
        }

        cryptoStream.FlushFinalBlock();
    }

    /// <summary>
    /// 使用公钥解密文件流（混合解密，解密由私钥加密的数据）
    /// </summary>
    /// <param name="inputStream">输入流</param>
    /// <param name="outputStream">输出流</param>
    /// <param name="publicKey">Base64 编码的公钥</param>
    /// <param name="bufferSize">缓冲区大小（默认 64KB）</param>
    /// <param name="progress">进度回调（参数：已处理字节数，总字节数）</param>
    public static void DecryptStreamWithPublicKey(Stream inputStream, Stream outputStream, string publicKey,
        int bufferSize = 65536, IProgress<(long processed, long total)>? progress = null)
    {
        if (inputStream == null)
            throw new ArgumentNullException(nameof(inputStream));
        if (outputStream == null)
            throw new ArgumentNullException(nameof(outputStream));
        if (string.IsNullOrEmpty(publicKey))
            throw new ArgumentNullException(nameof(publicKey));
        if (!inputStream.CanRead)
            throw new ArgumentException("输入流不可读", nameof(inputStream));
        if (!outputStream.CanWrite)
            throw new ArgumentException("输出流不可写", nameof(outputStream));

        // 读取并验证文件头
        var magic = new byte[FileHeaderMagic.Length];
        if (inputStream.Read(magic, 0, magic.Length) != magic.Length || !magic.SequenceEqual(FileHeaderMagic))
            throw new InvalidDataException("无效的加密文件格式");

        var version = inputStream.ReadByte();
        if (version != FileVersion)
            throw new InvalidDataException($"不支持的文件版本: {version}");

        // 读取加密的 AES 密钥
        var keyLengthBytes = new byte[4];
        inputStream.Read(keyLengthBytes, 0, 4);
        var keyLength = BitConverter.ToInt32(keyLengthBytes, 0);
        var encryptedKey = new byte[keyLength];
        inputStream.Read(encryptedKey, 0, keyLength);

        // 读取加密的 AES IV
        var ivLengthBytes = new byte[4];
        inputStream.Read(ivLengthBytes, 0, 4);
        var ivLength = BitConverter.ToInt32(ivLengthBytes, 0);
        var encryptedIV = new byte[ivLength];
        inputStream.Read(encryptedIV, 0, ivLength);

        // 使用 RSA 解密 AES 密钥和 IV
        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKey), out _);
        
        var aesKey = rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);
        var aesIV = rsa.Decrypt(encryptedIV, RSAEncryptionPadding.OaepSHA256);

        // 使用 AES 解密数据流
        using var aes = Aes.Create();
        aes.KeySize = AesKeySize;
        aes.Key = aesKey;
        aes.IV = aesIV;

        using var cryptoStream = new CryptoStream(inputStream, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
        
        var buffer = new byte[bufferSize];
        long totalBytes = inputStream.CanSeek ? inputStream.Length : 0;
        long processedBytes = 0;
        int bytesRead;

        while ((bytesRead = cryptoStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            outputStream.Write(buffer, 0, bytesRead);
            processedBytes += bytesRead;
            progress?.Report((processedBytes, totalBytes));
        }
    }

    /// <summary>
    /// 使用私钥加密文件（混合加密，用于验证真实性）
    /// </summary>
    /// <param name="inputFilePath">输入文件路径</param>
    /// <param name="outputFilePath">输出文件路径</param>
    /// <param name="privateKey">Base64 编码的私钥</param>
    /// <param name="progress">进度回调</param>
    public static void EncryptFileWithPrivateKey(string inputFilePath, string outputFilePath, string privateKey,
        IProgress<(long processed, long total)>? progress = null)
    {
        if (string.IsNullOrEmpty(inputFilePath))
            throw new ArgumentNullException(nameof(inputFilePath));
        if (string.IsNullOrEmpty(outputFilePath))
            throw new ArgumentNullException(nameof(outputFilePath));
        if (!File.Exists(inputFilePath))
            throw new FileNotFoundException("输入文件不存在", inputFilePath);

        using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        
        EncryptStreamWithPrivateKey(inputStream, outputStream, privateKey, progress: progress);
    }

    /// <summary>
    /// 使用公钥解密文件（混合解密，解密由私钥加密的数据）
    /// </summary>
    /// <param name="inputFilePath">输入文件路径</param>
    /// <param name="outputFilePath">输出文件路径</param>
    /// <param name="publicKey">Base64 编码的公钥</param>
    /// <param name="progress">进度回调</param>
    public static void DecryptFileWithPublicKey(string inputFilePath, string outputFilePath, string publicKey,
        IProgress<(long processed, long total)>? progress = null)
    {
        if (string.IsNullOrEmpty(inputFilePath))
            throw new ArgumentNullException(nameof(inputFilePath));
        if (string.IsNullOrEmpty(outputFilePath))
            throw new ArgumentNullException(nameof(outputFilePath));
        if (!File.Exists(inputFilePath))
            throw new FileNotFoundException("输入文件不存在", inputFilePath);

        using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        
        DecryptStreamWithPublicKey(inputStream, outputStream, publicKey, progress: progress);
    }
    /// <param name="progress">进度回调</param>
    public static void DecryptFile(string inputFilePath, string outputFilePath, string privateKey,
        IProgress<(long processed, long total)>? progress = null)
    {
        if (string.IsNullOrEmpty(inputFilePath))
            throw new ArgumentNullException(nameof(inputFilePath));
        if (string.IsNullOrEmpty(outputFilePath))
            throw new ArgumentNullException(nameof(outputFilePath));
        if (!File.Exists(inputFilePath))
            throw new FileNotFoundException("输入文件不存在", inputFilePath);

        using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        
        DecryptStream(inputStream, outputStream, privateKey, progress: progress);
    }

    /// <summary>
    /// 异步加密文件流
    /// </summary>
    public static async Task EncryptStreamAsync(Stream inputStream, Stream outputStream, string publicKey,
        int bufferSize = 65536, IProgress<(long processed, long total)>? progress = null, 
        CancellationToken cancellationToken = default)
    {
        if (inputStream == null)
            throw new ArgumentNullException(nameof(inputStream));
        if (outputStream == null)
            throw new ArgumentNullException(nameof(outputStream));
        if (string.IsNullOrEmpty(publicKey))
            throw new ArgumentNullException(nameof(publicKey));

        // 生成随机 AES 密钥和 IV
        using var aes = Aes.Create();
        aes.KeySize = AesKeySize;
        aes.GenerateKey();
        aes.GenerateIV();

        // 使用 RSA 加密 AES 密钥和 IV
        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKey), out _);
        
        var encryptedKey = rsa.Encrypt(aes.Key, RSAEncryptionPadding.OaepSHA256);
        var encryptedIV = rsa.Encrypt(aes.IV, RSAEncryptionPadding.OaepSHA256);

        // 写入文件头
        await outputStream.WriteAsync(FileHeaderMagic, 0, FileHeaderMagic.Length, cancellationToken);
        outputStream.WriteByte(FileVersion);
        await outputStream.WriteAsync(BitConverter.GetBytes(encryptedKey.Length), 0, 4, cancellationToken);
        await outputStream.WriteAsync(encryptedKey, 0, encryptedKey.Length, cancellationToken);
        await outputStream.WriteAsync(BitConverter.GetBytes(encryptedIV.Length), 0, 4, cancellationToken);
        await outputStream.WriteAsync(encryptedIV, 0, encryptedIV.Length, cancellationToken);

        // 使用 AES 加密数据流
        await using var cryptoStream = new CryptoStream(outputStream, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
        
        var buffer = new byte[bufferSize];
        long totalBytes = inputStream.CanSeek ? inputStream.Length : 0;
        long processedBytes = 0;
        int bytesRead;

        while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await cryptoStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            processedBytes += bytesRead;
            progress?.Report((processedBytes, totalBytes));
        }

        await cryptoStream.FlushFinalBlockAsync(cancellationToken);
    }

    /// <summary>
    /// 异步解密文件流
    /// </summary>
    public static async Task DecryptStreamAsync(Stream inputStream, Stream outputStream, string privateKey,
        int bufferSize = 65536, IProgress<(long processed, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (inputStream == null)
            throw new ArgumentNullException(nameof(inputStream));
        if (outputStream == null)
            throw new ArgumentNullException(nameof(outputStream));
        if (string.IsNullOrEmpty(privateKey))
            throw new ArgumentNullException(nameof(privateKey));

        // 读取并验证文件头
        var magic = new byte[FileHeaderMagic.Length];
        await inputStream.ReadAsync(magic, 0, magic.Length, cancellationToken);
        if (!magic.SequenceEqual(FileHeaderMagic))
            throw new InvalidDataException("无效的加密文件格式");

        var version = inputStream.ReadByte();
        if (version != FileVersion)
            throw new InvalidDataException($"不支持的文件版本: {version}");

        // 读取加密的 AES 密钥
        var keyLengthBytes = new byte[4];
        await inputStream.ReadAsync(keyLengthBytes, 0, 4, cancellationToken);
        var keyLength = BitConverter.ToInt32(keyLengthBytes, 0);
        var encryptedKey = new byte[keyLength];
        await inputStream.ReadAsync(encryptedKey, 0, keyLength, cancellationToken);

        // 读取加密的 AES IV
        var ivLengthBytes = new byte[4];
        await inputStream.ReadAsync(ivLengthBytes, 0, 4, cancellationToken);
        var ivLength = BitConverter.ToInt32(ivLengthBytes, 0);
        var encryptedIV = new byte[ivLength];
        await inputStream.ReadAsync(encryptedIV, 0, ivLength, cancellationToken);

        // 使用 RSA 解密 AES 密钥和 IV
        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(privateKey), out _);
        
        var aesKey = rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);
        var aesIV = rsa.Decrypt(encryptedIV, RSAEncryptionPadding.OaepSHA256);

        // 使用 AES 解密数据流
        using var aes = Aes.Create();
        aes.KeySize = AesKeySize;
        aes.Key = aesKey;
        aes.IV = aesIV;

        await using var cryptoStream = new CryptoStream(inputStream, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
        
        var buffer = new byte[bufferSize];
        long totalBytes = inputStream.CanSeek ? inputStream.Length : 0;
        long processedBytes = 0;
        int bytesRead;

        while ((bytesRead = await cryptoStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await outputStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            processedBytes += bytesRead;
            progress?.Report((processedBytes, totalBytes));
        }
    }

    /// <summary>
    /// 异步加密文件
    /// </summary>
    public static async Task EncryptFileAsync(string inputFilePath, string outputFilePath, string publicKey,
        IProgress<(long processed, long total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(inputFilePath))
            throw new ArgumentNullException(nameof(inputFilePath));
        if (string.IsNullOrEmpty(outputFilePath))
            throw new ArgumentNullException(nameof(outputFilePath));
        if (!File.Exists(inputFilePath))
            throw new FileNotFoundException("输入文件不存在", inputFilePath);

        await using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        await using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        
        await EncryptStreamAsync(inputStream, outputStream, publicKey, progress: progress, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 异步解密文件
    /// </summary>
    public static async Task DecryptFileAsync(string inputFilePath, string outputFilePath, string privateKey,
        IProgress<(long processed, long total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(inputFilePath))
            throw new ArgumentNullException(nameof(inputFilePath));
        if (string.IsNullOrEmpty(outputFilePath))
            throw new ArgumentNullException(nameof(outputFilePath));
        if (!File.Exists(inputFilePath))
            throw new FileNotFoundException("输入文件不存在", inputFilePath);

        await using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        await using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        
        await DecryptStreamAsync(inputStream, outputStream, privateKey, progress: progress, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 异步加密文件流（用私钥加密，用于验证真实性）
    /// </summary>
    public static async Task EncryptStreamWithPrivateKeyAsync(Stream inputStream, Stream outputStream, string privateKey,
        int bufferSize = 65536, IProgress<(long processed, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (inputStream == null)
            throw new ArgumentNullException(nameof(inputStream));
        if (outputStream == null)
            throw new ArgumentNullException(nameof(outputStream));
        if (string.IsNullOrEmpty(privateKey))
            throw new ArgumentNullException(nameof(privateKey));

        // 生成随机 AES 密钥和 IV
        using var aes = Aes.Create();
        aes.KeySize = AesKeySize;
        aes.GenerateKey();
        aes.GenerateIV();

        // 使用 RSA 加密 AES 密钥和 IV
        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPrivateKey(Convert.FromBase64String(privateKey), out _);
        
        var encryptedKey = rsa.Encrypt(aes.Key, RSAEncryptionPadding.OaepSHA256);
        var encryptedIV = rsa.Encrypt(aes.IV, RSAEncryptionPadding.OaepSHA256);

        // 写入文件头
        await outputStream.WriteAsync(FileHeaderMagic, 0, FileHeaderMagic.Length, cancellationToken);
        outputStream.WriteByte(FileVersion);
        await outputStream.WriteAsync(BitConverter.GetBytes(encryptedKey.Length), 0, 4, cancellationToken);
        await outputStream.WriteAsync(encryptedKey, 0, encryptedKey.Length, cancellationToken);
        await outputStream.WriteAsync(BitConverter.GetBytes(encryptedIV.Length), 0, 4, cancellationToken);
        await outputStream.WriteAsync(encryptedIV, 0, encryptedIV.Length, cancellationToken);

        // 使用 AES 加密数据流
        await using var cryptoStream = new CryptoStream(outputStream, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
        
        var buffer = new byte[bufferSize];
        long totalBytes = inputStream.CanSeek ? inputStream.Length : 0;
        long processedBytes = 0;
        int bytesRead;

        while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await cryptoStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            processedBytes += bytesRead;
            progress?.Report((processedBytes, totalBytes));
        }

        await cryptoStream.FlushFinalBlockAsync(cancellationToken);
    }

    /// <summary>
    /// 异步解密文件流（用公钥解密由私钥加密的数据）
    /// </summary>
    public static async Task DecryptStreamWithPublicKeyAsync(Stream inputStream, Stream outputStream, string publicKey,
        int bufferSize = 65536, IProgress<(long processed, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (inputStream == null)
            throw new ArgumentNullException(nameof(inputStream));
        if (outputStream == null)
            throw new ArgumentNullException(nameof(outputStream));
        if (string.IsNullOrEmpty(publicKey))
            throw new ArgumentNullException(nameof(publicKey));

        // 读取并验证文件头
        var magic = new byte[FileHeaderMagic.Length];
        await inputStream.ReadAsync(magic, 0, magic.Length, cancellationToken);
        if (!magic.SequenceEqual(FileHeaderMagic))
            throw new InvalidDataException("无效的加密文件格式");

        var version = inputStream.ReadByte();
        if (version != FileVersion)
            throw new InvalidDataException($"不支持的文件版本: {version}");

        // 读取加密的 AES 密钥
        var keyLengthBytes = new byte[4];
        await inputStream.ReadAsync(keyLengthBytes, 0, 4, cancellationToken);
        var keyLength = BitConverter.ToInt32(keyLengthBytes, 0);
        var encryptedKey = new byte[keyLength];
        await inputStream.ReadAsync(encryptedKey, 0, keyLength, cancellationToken);

        // 读取加密的 AES IV
        var ivLengthBytes = new byte[4];
        await inputStream.ReadAsync(ivLengthBytes, 0, 4, cancellationToken);
        var ivLength = BitConverter.ToInt32(ivLengthBytes, 0);
        var encryptedIV = new byte[ivLength];
        await inputStream.ReadAsync(encryptedIV, 0, ivLength, cancellationToken);

        // 使用 RSA 解密 AES 密钥和 IV
        using var rsa = RSA.Create(KeySize);
        rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKey), out _);
        
        var aesKey = rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);
        var aesIV = rsa.Decrypt(encryptedIV, RSAEncryptionPadding.OaepSHA256);

        // 使用 AES 解密数据流
        using var aes = Aes.Create();
        aes.KeySize = AesKeySize;
        aes.Key = aesKey;
        aes.IV = aesIV;

        await using var cryptoStream = new CryptoStream(inputStream, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
        
        var buffer = new byte[bufferSize];
        long totalBytes = inputStream.CanSeek ? inputStream.Length : 0;
        long processedBytes = 0;
        int bytesRead;

        while ((bytesRead = await cryptoStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await outputStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            processedBytes += bytesRead;
            progress?.Report((processedBytes, totalBytes));
        }
    }

    /// <summary>
    /// 异步加密文件（用私钥加密，用于验证真实性）
    /// </summary>
    public static async Task EncryptFileWithPrivateKeyAsync(string inputFilePath, string outputFilePath, string privateKey,
        IProgress<(long processed, long total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(inputFilePath))
            throw new ArgumentNullException(nameof(inputFilePath));
        if (string.IsNullOrEmpty(outputFilePath))
            throw new ArgumentNullException(nameof(outputFilePath));
        if (!File.Exists(inputFilePath))
            throw new FileNotFoundException("输入文件不存在", inputFilePath);

        await using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        await using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        
        await EncryptStreamWithPrivateKeyAsync(inputStream, outputStream, privateKey, progress: progress, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 异步解密文件（用公钥解密由私钥加密的数据）
    /// </summary>
    public static async Task DecryptFileWithPublicKeyAsync(string inputFilePath, string outputFilePath, string publicKey,
        IProgress<(long processed, long total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(inputFilePath))
            throw new ArgumentNullException(nameof(inputFilePath));
        if (string.IsNullOrEmpty(outputFilePath))
            throw new ArgumentNullException(nameof(outputFilePath));
        if (!File.Exists(inputFilePath))
            throw new FileNotFoundException("输入文件不存在", inputFilePath);

        await using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        await using var outputStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        
        await DecryptStreamWithPublicKeyAsync(inputStream, outputStream, publicKey, progress: progress, cancellationToken: cancellationToken);
    }

    #endregion
}
