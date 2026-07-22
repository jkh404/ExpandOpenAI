
using ExpandOpenAI;
using ExpandOpenAI.Providers.DashScope;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;




Console.OutputEncoding = Encoding.UTF8;

OpenAICompatibleReranker openAICompatibleReranker = new OpenAICompatibleReranker(new OpenAICompatibleRerankerOptions
{
    //Endpoint = new Uri("https://dashscope.aliyuncs.com/compatible-api/v1"),
    //ApiKey = "sk-",
    Endpoint = new Uri("http://b"),
    ApiKey = "sk-",
    ModelId = "qwen3-rerank",
});
var result=await openAICompatibleReranker.RerankAsync("什么是重排序模型", new[] {
    "量子计算是计算科学的前沿领域",
    "我们需要重新排序",
    "重排序模型的应用场景广泛",
    "预训练语言模型的发展为重排序模型带来了新的突破",

},new RerankingOptions { Instruct= "检索语义相似的文本",TopN=5 });
foreach(var item in result)
{
    Console.WriteLine($"Index={item.Index}, Score={item.RelevanceScore:F6}");
    Console.WriteLine(item.Document?.Text ?? "(document missing)");
    Console.WriteLine();
}
return;

//var client = new OpenAICompatibleChatClient(new OpenAICompatibleChatClientOptions
//{
//    Endpoint = new Uri("http://bid.xnscd.cn:50452/v1"),
//    ApiKey = "sk",
//    ModelId = "qwen3-asr-flash",
//});

//var msg = new ChatMessage
//{
//    Contents = [
//            new DashScopeAudioContent(new DataContent(File.ReadAllBytes(@"C:\Users\21017\Music\2026年05月12日 11点23分.mp3"), "audio/mpeg"))
//        ]
//};
//await foreach (var item in client.GetStreamingResponseAsync(msg))
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



#region 视觉理解
var client = new OpenAICompatibleChatClient(new OpenAICompatibleChatClientOptions
{
    Endpoint = new Uri("http://:50452/v1"),
    ApiKey = "sk-",
    ModelId = "qwen3.6-plus",
    RequestBody =new Dictionary<string,object?>
    {
        { "enable_thinking",false}
    }
});
//var client = new OpenAICompatibleChatClient(new OpenAICompatibleChatClientOptions
//{
//    Endpoint = new Uri("https://dashscope.aliyuncs.com/compatible-mode/v1"),
//    ApiKey = "sk",
//    ModelId = "qwen3.6-plus",
//    RequestBody =new Dictionary<string,object?>
//    {
//        { "enable_thinking",false}
//    }
//});

await foreach (var item in client.GetStreamingResponseAsync(new ChatMessage
{
    Contents = [
            new TextContent("请提取图片中的所有细节信息"),
            new Microsoft.Extensions.AI.DataContent(File.ReadAllBytes(@"C:\Users\21017\OneDrive\图片\test.png"),mediaType: "image/png")
        ]
}))
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
#endregion


//var result=await client.GetStreamingResponseAsync(new ChatMessage
//{
//    Contents = [
//            new TextContent("请提取图片中的所有细节信息"),
//            new Microsoft.Extensions.AI.DataContent(File.ReadAllBytes(@"C:\Users\21017\OneDrive\图片\test.png"),mediaType: "image/png")
//        ]
//}).ToChatResponseAsync();

//Console.WriteLine(result.Text);

