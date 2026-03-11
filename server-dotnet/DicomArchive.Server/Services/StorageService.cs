namespace DicomArchive.Server.Services;

/// <summary>
/// Retrieves DICOM blobs from the configured storage backend into a local
/// temp file for routing. Mirrors the logic in agent/storage.py.
/// </summary>
public class StorageService(IConfiguration config, ILogger<StorageService> logger)
{
    private readonly string _backend = config["STORAGE_BACKEND"] ?? "local";
    private readonly string _localBase = config["LOCAL_STORAGE_PATH"] ?? "./received";

    public async Task<string> FetchToTempAsync(string blobKey)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dcm_{Guid.NewGuid():N}.dcm");

        switch (_backend.ToLower())
        {
            case "local":
                await FetchLocalAsync(blobKey, tmp);
                break;
            case "s3":
                await FetchS3Async(blobKey, tmp);
                break;
            case "azure":
                await FetchAzureAsync(blobKey, tmp);
                break;
            default:
                throw new InvalidOperationException($"Unknown STORAGE_BACKEND: {_backend}");
        }

        logger.LogDebug("Fetched blob {Key} → {Tmp}", blobKey, tmp);
        return tmp;
    }

    private Task FetchLocalAsync(string blobKey, string dest)
    {
        var src = Path.Combine(_localBase, blobKey.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(src))
            throw new FileNotFoundException($"Blob not found: {src}");
        File.Copy(src, dest, overwrite: true);
        return Task.CompletedTask;
    }

    private Task FetchS3Async(string blobKey, string dest)
    {
        // S3 support requires adding AWSSDK.S3 package and uncommenting this implementation.
        // See INSTALL.md § Cloud Storage for setup instructions.
        throw new NotImplementedException(
            "S3 storage: add PackageReference for AWSSDK.S3 to DicomArchive.Server.csproj " +
            "then implement FetchS3Async using AmazonS3Client.");
    }

    private Task FetchAzureAsync(string blobKey, string dest)
    {
        // Azure support requires adding Azure.Storage.Blobs package.
        throw new NotImplementedException(
            "Azure storage: add PackageReference for Azure.Storage.Blobs to DicomArchive.Server.csproj " +
            "then implement FetchAzureAsync using BlobServiceClient.");
    }
}
