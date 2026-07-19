namespace OllMem.Storage.Firestore;

/// <summary>
/// Configuration for the Google Cloud Firestore persistence store. Firestore is
/// a fully managed cloud database, so every machine, device or agent pointed at
/// the same project shares one memory — no server of your own to run. Pairs
/// naturally with a cloud-deployed OllMem API (e.g. Firebase Hosting + Cloud
/// Run, see <c>install.sh --deploy-firebase</c>).
/// </summary>
public sealed class FirestoreStoreOptions
{
    /// <summary>Google Cloud / Firebase project id (required).</summary>
    public string ProjectId { get; set; } = "";

    /// <summary>Firestore database id (default: the project's default database).</summary>
    public string DatabaseId { get; set; } = "(default)";

    /// <summary>
    /// Prefix for the Firestore collections OllMem creates
    /// (<c>{prefix}_memories</c>, <c>{prefix}_blobs</c>, <c>{prefix}_audit</c>,
    /// <c>{prefix}_meta</c>). Lets several OllMem installations share a project.
    /// </summary>
    public string CollectionPrefix { get; set; } = "ollmem";

    /// <summary>
    /// Path to a service-account credentials JSON file. When null, Application
    /// Default Credentials are used (gcloud auth application-default login,
    /// GOOGLE_APPLICATION_CREDENTIALS, or the ambient identity on GCP).
    /// </summary>
    public string? CredentialsPath { get; set; }
}
