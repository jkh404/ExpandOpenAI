namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 内置记忆召回工具的检索范围。
/// </summary>
public enum MemoryRecallScope
{
    /// <summary>
    /// 同时检索会话层和全局层。
    /// </summary>
    All,

    /// <summary>
    /// 只检索当前会话记忆。
    /// </summary>
    Session,

    /// <summary>
    /// 只检索跨会话共享的全局记忆。
    /// </summary>
    Global,
}
