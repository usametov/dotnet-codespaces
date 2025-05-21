using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.EventGrid;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;

namespace S1Functions
{
    public class ClientData
    {
        [JsonProperty("id")]
        public string ConversationId { get; set; }
        public string Company { get; set; }
        public string Role { get; set; }
        public string Kpis { get; set; }
        public string Audience { get; set; }
        public string Uncertainties { get; set; }
        public string Themes { get; set; }
        public string Certainty { get; set; }
        public string Documents { get; set; }
        public List<Message> Messages { get; set; } = new List<Message>();
    }

    public class Message
    {
        public string Role { get; set; }
        public string Content { get; set; }
    }

    public class CustomEvent
    {
        public string Id { get; set; }
        public string EventType { get; set; }
        public string Subject { get; set; }
        public string EventTime { get; set; }
        public JObject Data { get; set; }
        public string DataVersion { get; set; }
    }

    public static class ChatbotFunctions
    {
        private static readonly string eventGridEndpoint = "https://<your-eventgrid-topic>.westus2-1.eventgrid.azure.net/api/events";
        private static readonly string eventGridKey = "<your-eventgrid-key>";
        private static readonly string openAiApiKey = "<your-openai-api-key>";
        private static readonly string serpApiKey = "<your-serpapi-key>";
        private static readonly string cosmosEndpoint = "<your-cosmos-endpoint>";
        private static readonly string cosmosKey = "<your-cosmos-key>";
        private static readonly string cosmosDatabaseId = "ChatbotDB";
        private static readonly string cosmosContainerId = "Conversations";

        private static readonly CosmosClient cosmosClient = new CosmosClient(cosmosEndpoint, cosmosKey);
        private static readonly Container container = cosmosClient.GetContainer(cosmosDatabaseId, cosmosContainerId);
        private static readonly HttpClient httpClient = new HttpClient();

        [FunctionName("ChatTrigger")]
        public static async Task<IActionResult> ChatTrigger(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Chat trigger function processed a request.");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            string message = data?.message;
            string conversationId = data?.conversationId ?? $"conv_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            if (string.IsNullOrEmpty(message))
            {
                return new BadRequestObjectResult("Message is required");
            }

            var eventGridClient = new EventGridClient(new TopicCredentials(eventGridKey));
            var eventToSend = new EventGridEvent
            {
                Id = $"{conversationId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                EventType = "ChatMessageReceived",
                Subject = $"chatbot/{conversationId}",
                EventTime = DateTime.UtcNow,
                Data = new { conversationId, message },
                DataVersion = "1.0"
            };

            await eventGridClient.PublishEventsAsync(new Uri(eventGridEndpoint).Host, new List<EventGridEvent> { eventToSend });

            return new OkObjectResult(new { conversationId, message = "Message received" });
        }

        [FunctionName("S1OnboardingAgent")]
        public static async Task<IActionResult> S1OnboardingAgent(
            [EventGridTrigger] CustomEvent eventGridEvent,
            ILogger log)
        {
            log.LogInformation($"S1 Onboarding Agent processing event: {JsonConvert.SerializeObject(eventGridEvent)}");

            if (eventGridEvent.EventType != "ChatMessageReceived")
            {
                return new OkObjectResult(new { message = "Event ignored" });
            }

            string conversationId = eventGridEvent.Data["conversationId"]?.ToString();
            string message = eventGridEvent.Data["message"]?.ToString();

            ClientData clientData = new ClientData { ConversationId = conversationId };
            try
            {
                var response = await container.ReadItemAsync<ClientData>(conversationId, new PartitionKey(conversationId));
                clientData = response.Resource ?? clientData;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                log.LogInformation("No existing conversation found, starting new one.");
            }

            clientData.Messages.Add(new Message { Role = "user", Content = message });

            string[] questions = new[]
            {
                "In a brief summary, tell me where you work.",
                "Describe your role and responsibilities.",
                "Describe your target audience for the survey.",
                "What are the key uncertainties that you’d like to ask your audience?",
                "What themes, products, or services are most important now?",
                "What would certainty feel like to you?",
                "Do you have any documents to help us understand your goals?"
            };

            int answeredQuestions = new[] { "Company", "Role", "Audience", "Uncertainties", "Themes", "Certainty", "Documents" }
                .Count(prop => typeof(ClientData).GetProperty(prop).GetValue(clientData) != null);
            string response;

            if (answeredQuestions < questions.Length)
            {
                response = questions[answeredQuestions];

                if (answeredQuestions > 0 && message.ToLower() != "yes" && message.ToLower() != "no")
                {
                    string[] fieldMap = new[] { "Company", "Role", "Audience", "Uncertainties", "Themes", "Certainty", "Documents" };
                    typeof(ClientData).GetProperty(fieldMap[answeredQuestions - 1]).SetValue(clientData, message);

                    if (answeredQuestions == 0 && !string.IsNullOrEmpty(message))
                    {
                        try
                        {
                            var serpResponse = await httpClient.GetAsync($"https://serpapi.com/search?q={Uri.EscapeDataString(message)}&api_key={serpApiKey}");
                            var serpData = await serpResponse.Content.ReadAsStringAsync();
                            dynamic serpJson = JsonConvert.DeserializeObject(serpData);
                            clientData.Messages.Add(new Message
                            {
                                Role = "system",
                                Content = $"Context from search: {serpJson?.organic_results?[0]?.snippet}"
                            });
                        }
                        catch (Exception ex)
                        {
                            log.LogInformation($"SerpAPI error: {ex.Message}");
                        }
                    }

                    response = $"Just so I understand, you said {message} for {fieldMap[answeredQuestions - 1].ToLower()}? Please confirm with 'Yes' or 'No'.";
                }
                else if (message.ToLower() == "yes")
                {
                    response = questions[answeredQuestions];
                }
                else if (message.ToLower() == "no")
                {
                    response = $"Could you clarify your answer for {questions[answeredQuestions - 1]}?";
                }
            }
            else if (message.ToLower() == "approved")
            {
                var eventGridClient = new EventGridClient(new TopicCredentials(eventGridKey));
                var eventToSend = new EventGridEvent
                {
                    Id = $"{conversationId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    EventType = "ClientDataCollected",
                    Subject = $"chatbot/{conversationId}",
                    EventTime = DateTime.UtcNow,
                    Data = clientData,
                    DataVersion = "1.0"
                };
                await eventGridClient.PublishEventsAsync(new Uri(eventGridEndpoint).Host, new List<EventGridEvent> { eventToSend });
                response = "Let's generate your canvass and deploy your first game!";
            }
            else
            {
                response = $"Here's what I gathered:\n- Company: {clientData.Company ?? "N/A"}\n- Role: {clientData.Role ?? "N/A"}\n- Audience: {clientData.Audience ?? "N/A"}\n- Uncertainties: {clientData.Uncertainties ?? "N/A"}\n- Themes: {clientData.Themes ?? "N/A"}\n- Certainty: {clientData.Certainty ?? "N/A"}\n- Documents: {clientData.Documents ?? "N/A"}\nIs this correct? Please respond with 'Approved' to proceed.";
            }

            try
            {
                var openAiPayload = new
                {
                    model = "gpt-4o-mini",
                    messages = new object[]
                    {
                        new { role = "system", content = "You are August Says, a cheerful and intuitive sentiment tool." },
                        clientData.Messages.Select(m => new { m.Role, m.Content }).ToArray(),
                        new { role = "assistant", content = response }
                    }
                };

                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", openAiApiKey);
                var openAiResponse = await httpClient.PostAsync("https://api.openai.com/v1/chat/completions", 
                    new StringContent(JsonConvert.SerializeObject(openAiPayload), Encoding.UTF8, "application/json"));
                dynamic openAiData = JsonConvert.DeserializeObject(await openAiResponse.Content.ReadAsStringAsync());
                response = openAiData.choices[0].message.content;
            }
            catch (Exception ex)
            {
                log.LogInformation($"OpenAI error: {ex.Message}");
            }

            clientData.Messages.Add(new Message { Role = "assistant", Content = response });
            await container.UpsertItemAsync(clientData, new PartitionKey(conversationId));

            return new OkObjectResult(new { conversationId, message = response });
        }

        [FunctionName("S2BriefAgent")]
        public static async Task<IActionResult> S2BriefAgent(
            [EventGridTrigger] CustomEvent eventGridEvent,
            ILogger log)
        {
            log.LogInformation($"S2 Brief Agent processing event: {JsonConvert.SerializeObject(eventGridEvent)}");

            if (eventGridEvent.EventType != "ClientDataCollected")
            {
                return new OkObjectResult(new { message = "Event ignored" });
            }

            var clientData = JsonConvert.DeserializeObject<ClientData>(eventGridEvent.Data.ToString());
            string brief = $"Client Brief:\n- Company: {clientData.Company}\n- Role: {clientData.Role}\n- Audience: {clientData.Audience}\n- Priorities: {clientData.Themes}\n- Uncertainties: {clientData.Uncertainties}";

            var eventGridClient = new EventGridClient(new TopicCredentials(eventGridKey));
            var eventToSend = new EventGridEvent
            {
                Id = $"{clientData.ConversationId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                EventType = "BriefGenerated",
                Subject = $"chatbot/{clientData.ConversationId}",
                EventTime = DateTime.UtcNow,
                Data = new { conversationId = clientData.ConversationId, brief },
                DataVersion = "1.0"
            };
            await eventGridClient.PublishEventsAsync(new Uri(eventGridEndpoint).Host, new List<EventGridEvent> { eventToSend });

            return new OkObjectResult(new { message = "Brief generated" });
        }

        [FunctionName("S3CanvassGenerator")]
        public static async Task<IActionResult> S3CanvassGenerator(
            [EventGridTrigger] CustomEvent eventGridEvent,
            ILogger log)
        {
            log.LogInformation($"S3 Canvass Generator processing event: {JsonConvert.SerializeObject(eventGridEvent)}");

            if (eventGridEvent.EventType == "BriefGenerated")
            {
                string conversationId = eventGridEvent.Data["conversationId"]?.ToString();
                string brief = eventGridEvent.Data["brief"]?.ToString();

                string canvass = $"Canvass Report for {conversationId}:\n{brief}\nGenerated on {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}";

                var eventGridClient = new EventGridClient(new TopicCredentials(eventGridKey));
                var eventToSend = new EventGridEvent
                {
                    Id = $"{conversationId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    EventType = "CanvassGenerated",
                    Subject = $"chatbot/{conversationId}",
                    EventTime = DateTime.UtcNow,
                    Data = new { conversationId, canvass },
                    DataVersion = "1.0"
                };
                await eventGridClient.PublishEventsAsync(new Uri(eventGridEndpoint).Host, new List<EventGridEvent> { eventToSend });

                return new OkObjectResult(new { message = "Canvass generated, please provide an email for the PDF report" });
            }
            else if (eventGridEvent.EventType == "EmailProvided")
            {
                string email = eventGridEvent.Data["email"]?.ToString();
                string canvass = eventGridEvent.Data["canvass"]?.ToString();

                log.LogInformation($"Sending canvass to {email}:\n{canvass}");

                return new OkObjectResult(new { message = "Thank you for using August Says! Your canvass report has been sent. Goodbye!" });
            }

            return new OkObjectResult(new { message = "Event ignored" });
        }
    }
}

// using Microsoft.AspNetCore.OpenApi;
// using Scalar.AspNetCore;

// var builder = WebApplication.CreateBuilder(args);

// // Add services to the container.
// // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
// builder.Services.AddOpenApi(options =>
// {
//     // current workaround for port forwarding in codespaces
//     // https://github.com/dotnet/aspnetcore/issues/57332
//     options.AddDocumentTransformer((document, context, ct) =>
//     {
//         document.Servers = [];
//         return Task.CompletedTask;
//     });
// });

// var app = builder.Build();

// // Configure the HTTP request pipeline.
// if (app.Environment.IsDevelopment())
// {
//     app.MapOpenApi();
//     app.MapScalarApiReference();
// }

// app.UseHttpsRedirection();

// var summaries = new[]
// {
//     "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
// };

// app.MapGet("/weatherforecast", () =>
// {
//     var forecast = Enumerable.Range(1, 5).Select(index =>
//         new WeatherForecast
//         (
//             DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
//             Random.Shared.Next(-20, 55),
//             summaries[Random.Shared.Next(summaries.Length)]
//         ))
//         .ToArray();
//     return forecast;
// })
// .WithName("GetWeatherForecast");

// app.Run();

// internal record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
// {
//     public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
// }


