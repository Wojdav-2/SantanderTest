using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<JsonService>();
builder.Services.AddSingleton<EndpointOptions>((provider) =>
{
    var options = new EndpointOptions();
    var configuration = provider.GetRequiredService<IConfiguration>();
    configuration.Bind("EndpointOptions", options);
    return options;
});

var webApplication = builder.Build();

webApplication.UseHttpsRedirection();

webApplication.MapGet("/BestStories/{Count}", async (int count, [FromServices] IMemoryCache memoryCache, [FromServices] JsonService jsonService, [FromServices] EndpointOptions endpointOptions, [FromServices] IServiceProvider provider) =>
{
    var resultList = new List<StoryDto>();

    try
    {
        var httpClient = new HttpClient();

        var idsResponse = await httpClient.GetAsync(endpointOptions.BaseUrl + endpointOptions.DataSetUrl);
        idsResponse.EnsureSuccessStatusCode();

        var idsJsonStream = await idsResponse.Content.ReadAsStreamAsync();
        var ids = await jsonService.DeserializeAsync<List<int>>(idsJsonStream);

        var maxItems = Math.Min(count, endpointOptions.MaxItemsPerRequest);

        var tasks = ids.Take(maxItems).Where(id =>
        {
            var exist = memoryCache.TryGetValue(id, out object? value);
            if (exist)
            {
                var r = (StoryDto) value!;
                resultList.Add(r);
            }

            return !exist;
        }).Select(id => httpClient.GetAsync(endpointOptions.BaseUrl + string.Format(endpointOptions.ItemUrl, id)));
        var storyResponses = await Task.WhenAll(tasks);

        foreach (var item in storyResponses)
        {
            item.EnsureSuccessStatusCode();

            var stream = await item.Content.ReadAsStreamAsync();
            var deserializedItem = await jsonService.DeserializeAsync<JsonElement>(stream);
            var story = new StoryDto(
                deserializedItem.GetProperty("title").GetString()!,
                deserializedItem.GetProperty("url").GetString()!,
                deserializedItem.GetProperty("by").GetString()!,
                deserializedItem.GetProperty("time").GetInt32(),
                deserializedItem.GetProperty("score").GetInt32(),
                deserializedItem.GetProperty("descendants").GetInt32()
            );

            memoryCache.Set(deserializedItem.GetProperty("id").GetInt32(), story, TimeSpan.FromSeconds(endpointOptions.CacheDurationInSeconds));

            resultList.Add(story);
        }
    }
    catch (Exception exception)
    {
        if (builder.Environment.IsProduction())
        {
            return Results.Problem("Hacker News Api does not work.", Environment.MachineName, 500);
        }

        throw;
    }

    return Results.Ok(resultList);
}).WithName("BestStories");

webApplication.Run();

internal class JsonService
{
    public async Task<T> DeserializeAsync<T>(Stream jsonStream)
    {
        if (jsonStream == null)
        {
            throw new ArgumentNullException(nameof(jsonStream));
        }

        var result = await JsonSerializer.DeserializeAsync<T>(jsonStream) ?? throw new Exception("JsonService had problem during serialization.");
        return result;
    }
}

internal class EndpointOptions()
{
    public string BaseUrl { get; init; } = null!;
    public string DataSetUrl { get; init; } = null!;
    public string ItemUrl { get; init; } = null!;
    public int MaxItemsPerRequest { get; init; }
    public int CacheDurationInSeconds { get; init; }
}

internal record class StoryDto(string Title, string Uri, string PostedBy, int Time, int Score, int CommentCount);