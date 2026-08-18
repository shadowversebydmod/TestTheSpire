using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Platform;

namespace TestTheSpire;

internal sealed class TestNetGameService(ulong netId, NetGameType type = NetGameType.Host) : INetGameService
{
    private bool _isGameLoading;

    public ulong NetId => netId;

    public bool IsConnected => true;

    public bool IsGameLoading => _isGameLoading;

    public NetGameType Type => type;

    public PlatformType Platform => PlatformType.None;

    public PeerVersionInfo LocalVersion { get; } = PeerVersionInfo.LocalDefault();

    public event Action<NetErrorInfo>? Disconnected;

    public void SendMessage<T>(T message, ulong playerId) where T : INetMessage { }

    public void SendMessage<T>(T message) where T : INetMessage { }

    public void RegisterMessageHandler<T>(MessageHandlerDelegate<T> messageHandlerDelegate)
        where T : INetMessage { }

    public void UnregisterMessageHandler<T>(MessageHandlerDelegate<T> messageHandlerDelegate)
        where T : INetMessage { }

    public void Update() { }

    public void Disconnect(NetError reason, bool now = false)
    {
        Disconnected?.Invoke(new NetErrorInfo(reason, true));
    }

    public ConnectionStats? GetStatsForPeer(ulong peerId)
    {
        return null;
    }

    public void SetGameLoading(bool isLoading)
    {
        _isGameLoading = isLoading;
    }

    public void SetBufferMessages(bool bufferMessages) { }

    public string? GetRawLobbyIdentifier()
    {
        return null;
    }
}
