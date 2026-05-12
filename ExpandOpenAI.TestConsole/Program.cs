
using ExpandOpenAI;
using ExpandOpenAI.Providers.DashScope;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.Net;





var client = new DefaultChatClient(new DefaultChatClientOptions
{
    Endpoint = new Uri("https://dashscope.aliyuncs.com/compatible-mode/v1"),
    ApiKey = "",
    ModelId = "qwen3-asr-flash",
});

var msg = new ChatMessage
{
    Contents = [
            new DashScopeAudioContent(new DataContent(File.ReadAllBytes(@"C:\Users\21017\Music\2026年05月12日 11点23分.mp3"), "audio/mpeg"))
        ]
};
await foreach (var item in client.GetStreamingResponseAsync(msg))
{
    if (item.Contents.Count > 0)
    {
        foreach (var content in item.Contents)
        {
            if (content is TextReasoningContent reasoningContent)
            {
                Console.BackgroundColor = ConsoleColor.Red;
                Console.Write(reasoningContent.Text);
            }
            else if (content is TextContent textContent)
            {
                if (Console.BackgroundColor == ConsoleColor.Red)
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.WriteLine();
                }

                Console.Write(textContent.Text);
            }
        }
    }

}



#region 视觉理解
//var client = new DefaultChatClient(new DefaultChatClientOptions
//{
//      Endpoint = new Uri("https://dashscope.aliyuncs.com/compatible-mode/v1"),
//      ApiKey = "",
//    ModelId = "qwen3.6-plus",
//});

//await foreach (var item in client.GetStreamingResponseAsync(new ChatMessage
//{
//    Contents = [
//            new TextContent("请提取图片中的所有细节信息"),
//            new Microsoft.Extensions.AI.DataContent(File.ReadAllBytes(@"C:\Users\21017\OneDrive\图片\test.png"),mediaType: "image/png")
//        ]
//}))
//{
//    if (item.Contents.Count > 0)
//    {
//        foreach (var content in item.Contents)
//        {
//            if (content is TextReasoningContent reasoningContent)
//            {
//                Console.BackgroundColor = ConsoleColor.Red;
//                Console.Write(reasoningContent.Text);
//            }
//            else if (content is TextContent textContent)
//            {
//                if (Console.BackgroundColor == ConsoleColor.Red)
//                {
//                    Console.BackgroundColor = ConsoleColor.Black;
//                    Console.WriteLine();
//                }

//                Console.Write(textContent.Text);
//            }
//        }
//    }

//}
#endregion


//var result=await client.GetStreamingResponseAsync(new ChatMessage
//{
//    Contents = [
//            new TextContent("请提取图片中的所有细节信息"),
//            new Microsoft.Extensions.AI.DataContent(File.ReadAllBytes(@"C:\Users\21017\OneDrive\图片\test.png"),mediaType: "image/png")
//        ]
//}).ToChatResponseAsync();

//Console.WriteLine(result.Text);

