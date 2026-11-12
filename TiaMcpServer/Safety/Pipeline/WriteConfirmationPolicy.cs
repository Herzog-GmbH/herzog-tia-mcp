using TiaMcpServer.Contracts.Safety;

namespace TiaMcpServer.Safety.Pipeline;

public enum ConfirmationMode { AskUser, Policy }

/// <summary>Full and Produktiv satisfy acknowledge guards by policy; all other modes ask the client.</summary>
public static class WriteConfirmationPolicy
{
    public static ConfirmationMode For(McpAccessMode mode)
        => mode is McpAccessMode.Full or McpAccessMode.Produktiv ? ConfirmationMode.Policy : ConfirmationMode.AskUser;
}
