using System.Runtime.Versioning;
using Beacon.Companion.Core.Credentials;
using Beacon.Companion.Core.Heartbeat;
using Beacon.Companion.Core.Input;
using Beacon.Companion.Core.Security;
using Beacon.Companion.Windows.Credentials;

namespace Beacon.Companion.Windows.Heartbeat;

/// <summary>
/// Windows production composition factory for <see cref="HeartbeatSupervisor"/>.
/// Guarantees that every Windows production supervisor instance is backed by a durable
/// DPAPI-encrypted <see cref="DpapiRevocationTombstoneStore"/> (<c>DataProtectionScope.CurrentUser</c>)
/// and can never silently fall back to an in-memory tombstone store.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsHeartbeatSupervisorFactory
{
    public static HeartbeatSupervisor Create(
        ICredentialStore credentialStore,
        IHeartbeatClient heartbeatClient,
        DpapiRevocationTombstoneStore? tombstoneStore = null,
        HeartbeatSupervisorOptions? options = null,
        SessionStateMachine? sessionStateMachine = null,
        IInputExecutor? inputExecutor = null,
        TimeProvider? timeProvider = null,
        IJitterSource? jitterSource = null)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(heartbeatClient);

        var durableTombstoneStore = tombstoneStore ?? new DpapiRevocationTombstoneStore();
        return new HeartbeatSupervisor(
            credentialStore,
            heartbeatClient,
            durableTombstoneStore,
            options,
            sessionStateMachine,
            inputExecutor,
            timeProvider,
            jitterSource);
    }
}
