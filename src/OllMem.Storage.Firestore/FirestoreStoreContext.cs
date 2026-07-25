using Google.Api.Gax;
using Google.Cloud.Firestore;

namespace OllMem.Storage.Firestore;

/// <summary>
/// Owns the <see cref="FirestoreDb"/> connection and the collection names. The
/// client is thread-safe and intended to be a process-wide singleton. When the
/// <c>FIRESTORE_EMULATOR_HOST</c> environment variable is set the local
/// Firestore emulator is used automatically (handy for tests and offline dev).
/// </summary>
public sealed class FirestoreStoreContext
{
    public FirestoreDb Db { get; }
    public FirestoreStoreOptions Options { get; }

    public string MemoriesCollection { get; }
    public string BlobsCollection { get; }
    public string AuditCollection { get; }
    public string MetaCollection { get; }
    public string OwnerLinksCollection { get; }

    public FirestoreStoreContext(FirestoreStoreOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectId))
            throw new ArgumentException("FirestoreStoreOptions.ProjectId is required.", nameof(options));

        Options = options;
        var prefix = string.IsNullOrWhiteSpace(options.CollectionPrefix) ? "ollmem" : options.CollectionPrefix;
        MemoriesCollection = $"{prefix}_memories";
        BlobsCollection = $"{prefix}_blobs";
        AuditCollection = $"{prefix}_audit";
        MetaCollection = $"{prefix}_meta";
        OwnerLinksCollection = $"{prefix}_owner_links";

        var builder = new FirestoreDbBuilder
        {
            ProjectId = options.ProjectId,
            DatabaseId = options.DatabaseId,
            EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
        };
        if (!string.IsNullOrWhiteSpace(options.CredentialsPath))
            builder.CredentialsPath = options.CredentialsPath;

        Db = builder.Build();
    }

    public CollectionReference Memories => Db.Collection(MemoriesCollection);
    public CollectionReference Blobs => Db.Collection(BlobsCollection);
    public CollectionReference Audit => Db.Collection(AuditCollection);
    public DocumentReference AuditTip => Db.Collection(MetaCollection).Document("audit_tip");
    public CollectionReference OwnerLinks => Db.Collection(OwnerLinksCollection);
}
