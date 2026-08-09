using CamusDB.Client;

namespace CamusDB.WebConsole.Services;

/// <summary>
/// The node's online backup administration: taking backups, listing the catalog, resolving a restore
/// chain, and running retention. All are safe while the server serves traffic.
///
/// <para><b>Node-wide, not per-database.</b> Every database on a CamusDB server shares one storage
/// node, so a backup captures all of them at once — nothing here is scoped to the session's current
/// database, which is why this lives in Administration rather than beside the schema explorer.</para>
///
/// <para><b>Restore is deliberately absent.</b> It rebuilds into a fresh data root and the operator
/// then stops the server and boots a new one against it, so it is not an operation a web console can
/// drive to completion. The chain view answers the question a console can usefully answer — "would
/// this backup actually restore?" — and leaves the restore itself to the runbook.</para>
/// </summary>
public sealed class BackupService
{
    private readonly CamusSessionService _session;

    public BackupService(CamusSessionService session)
    {
        _session = session;
    }

    /// <summary>
    /// Why the backup surface cannot be used right now, or null when it can be attempted. This covers
    /// only what the console can know without asking the server; everything else (backups not
    /// configured, not the coordinator, loopback-only) surfaces as a translated error from the call.
    /// </summary>
    public string? UnavailableReason
    {
        get
        {
            if (!_session.IsConnected)
                return "Not connected.";

            if (_session.EffectiveBackupEndpoint is null)
            {
                return "The backup API is REST-only and this session speaks gRPC. Set a backup endpoint "
                    + "(CamusDB:BackupEndpoint, or the field in Configure) to the server's HTTP endpoint.";
            }

            return null;
        }
    }

    private CamusBackupClient Backups => _session.GetConnection().Backups;

    public Task<IReadOnlyList<CamusBackupInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        Backups.ListBackupsAsync(cancellationToken);

    /// <summary>
    /// Resolves the chain ending at <paramref name="leafBackupId"/>, root-first. This is the validating
    /// read: a chain that could not be assembled is rejected here rather than at restore time, so it
    /// doubles as a "would this backup actually restore?" check. The chain's recoverable window is
    /// reported on the <b>root</b> entry, not the leaf.
    /// </summary>
    public Task<IReadOnlyList<CamusBackupInfo>> GetChainAsync(
        string leafBackupId,
        CancellationToken cancellationToken = default) =>
        Backups.GetChainAsync(leafBackupId, cancellationToken);

    public Task<CamusBackupInfo> TakeFullAsync(CancellationToken cancellationToken = default) =>
        Backups.TakeFullBackupAsync(cancellationToken);

    /// <summary>
    /// Chains an incremental onto <paramref name="parentBackupId"/>. If the parent has aged past the
    /// retention floor the server takes a <em>full</em> backup instead and still succeeds — check
    /// <see cref="CamusBackupInfo.WasSubstituted"/> on the result, which the UI reports rather than
    /// letting a full backup pass for an increment.
    /// </summary>
    public Task<CamusBackupInfo> TakeIncrementalAsync(
        string parentBackupId,
        CancellationToken cancellationToken = default) =>
        Backups.TakeIncrementalBackupAsync(parentBackupId, cancellationToken);

    /// <summary>
    /// Takes one consistent HLC cut across every partition. Must reach the coordinator; another node
    /// refuses with <see cref="CamusSessionService.BackupNotCoordinatorCode"/>.
    /// </summary>
    public Task<CamusBackupInfo> TakeCoordinatedAsync(CancellationToken cancellationToken = default) =>
        Backups.TakeCoordinatedBackupAsync(cancellationToken);

    /// <summary>Reports what retention would reclaim right now, deleting nothing.</summary>
    public Task<CamusBackupGcResult> PreviewGarbageCollectionAsync(CancellationToken cancellationToken = default) =>
        Backups.PreviewGarbageCollectionAsync(cancellationToken);

    public Task<CamusBackupGcResult> CollectGarbageAsync(CancellationToken cancellationToken = default) =>
        Backups.CollectGarbageAsync(cancellationToken);
}
