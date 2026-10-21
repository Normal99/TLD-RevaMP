// Only compiled when the real Steamworks assembly is absent. The test's NetLab
// records these config keys; NetworkConditions itself is the production code.
namespace Steamworks
{
    public enum ESteamNetworkingConfigValue
    {
        k_ESteamNetworkingConfig_FakePacketLoss_Send = 2,
        k_ESteamNetworkingConfig_FakePacketLoss_Recv = 3,
        k_ESteamNetworkingConfig_FakePacketLag_Send = 4,
        k_ESteamNetworkingConfig_FakePacketLag_Recv = 5,
        k_ESteamNetworkingConfig_FakePacketReorder_Send = 6,
        k_ESteamNetworkingConfig_FakePacketReorder_Recv = 7,
        k_ESteamNetworkingConfig_FakePacketReorder_Time = 8,
        k_ESteamNetworkingConfig_FakePacketDup_Send = 26,
        k_ESteamNetworkingConfig_FakePacketDup_Recv = 27,
        k_ESteamNetworkingConfig_FakePacketDup_TimeMax = 28
    }
}
