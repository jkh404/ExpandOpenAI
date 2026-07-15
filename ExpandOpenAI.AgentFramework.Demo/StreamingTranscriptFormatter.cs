using System.Text;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework.Demo;

/// <summary>
/// 将模型流式更新转换为可直接追加到网页对话区的文本。
/// </summary>
internal sealed class StreamingTranscriptFormatter
{
    private SegmentKind _previousSegment;

    public string Format(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var output = new StringBuilder();
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    BeginSegment(output, SegmentKind.Reasoning, "[思考]\n");
                    output.Append(reasoning.Text);
                    break;
                case TextContent text:
                    BeginSegment(output, SegmentKind.Text, string.Empty);
                    output.Append(text.Text);
                    break;
                case FunctionCallContent call:
                    BeginSegment(output, SegmentKind.Tool, string.Empty);
                    output.Append("[工具调用: ").Append(call.Name).Append("]\n");
                    break;
                case FunctionResultContent result:
                    BeginSegment(output, SegmentKind.Tool, string.Empty);
                    output.Append("[工具结果: ").Append(result.Result).Append("]\n");
                    break;
            }
        }

        return output.ToString();
    }

    private void BeginSegment(StringBuilder output, SegmentKind next, string heading)
    {
        if (_previousSegment == next)
        {
            return;
        }

        if (_previousSegment != SegmentKind.None)
        {
            output.Append("\n\n");
        }

        output.Append(heading);
        _previousSegment = next;
    }

    private enum SegmentKind
    {
        None,
        Reasoning,
        Text,
        Tool,
    }
}
