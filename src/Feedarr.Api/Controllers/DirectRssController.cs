using System.ComponentModel.DataAnnotations;
using Feedarr.Api.Data.Repositories;
using Feedarr.Api.Models;
using Feedarr.Api.Services.Categories;
using Feedarr.Api.Services.DirectRss;
using Feedarr.Api.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace Feedarr.Api.Controllers;

[ApiController]
[Route("api/sources/rss")]
public sealed class DirectRssController(SourceRepository sources, DirectRssRepository feeds, DirectRssClient client) : ControllerBase
{
    [HttpGet("{id:long}/feeds")]
    public IActionResult List(long id) => sources.Get(id)?.SourceKind != "rss"
        ? NotFound() : Ok(feeds.List(id));

    [HttpPost]
    public IActionResult Create(RssSourceRequest request) => Save(null, request);

    [HttpPut("{id:long}")]
    public IActionResult Update(long id, RssSourceRequest request) => Save(id, request);

    private IActionResult Save(long? id, RssSourceRequest request)
    {
        if (id is not null && sources.Get(id.Value)?.SourceKind != "rss") return NotFound();
        var previous = id is null ? new List<DirectRssFeed>() : feeds.List(id.Value);
        var entries = new List<DirectRssFeed>();
        if (request.Feeds.Count > 100 || request.Feeds.Select(f => f.Id).Where(n => n > 0).Distinct().Count() != request.Feeds.Count(f => f.Id > 0))
            return BadRequest(new { error = "Invalid RSS feed list" });
        foreach (var feed in request.Feeds)
        {
            var old = previous.SingleOrDefault(f => f.Id == feed.Id);
            if (feed.Id < 0 || (feed.Id > 0 && old is null)) return BadRequest(new { error = "Unknown RSS feed" });
            var url = string.IsNullOrWhiteSpace(feed.Url) ? old?.Url : feed.Url.Trim();
            if (!OutboundUrlGuard.TryNormalizeTorznabUrl(url, out _, out _) ||
                !CategoryGroupCatalog.TryNormalizeKey(feed.CategoryKey, out var category))
                return BadRequest(new { error = "RSS URL or Feedarr category invalid" });
            entries.Add(new() { Id = feed.Id, Url = url!, Name = feed.Name?.Trim(), CategoryKey = category, Enabled = feed.Enabled });
        }
        var sourceId = feeds.Save(id, request.Name.Trim(), request.Color, entries);
        return Ok(new { id = sourceId });
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test(RssFeedTestRequest request, CancellationToken ct)
    {
        var url = request.Url;
        if (string.IsNullOrWhiteSpace(url) && request.SourceId is long sourceId && request.Id > 0)
        {
            if (sources.Get(sourceId)?.SourceKind != "rss") return NotFound();
            url = feeds.List(sourceId).SingleOrDefault(f => f.Id == request.Id)?.Url;
        }
        if (!OutboundUrlGuard.TryNormalizeTorznabUrl(url, out _, out _) ||
            !CategoryGroupCatalog.TryNormalizeKey(request.CategoryKey, out var category))
            return BadRequest(new { error = "RSS URL or Feedarr category invalid" });
        try
        {
            var entries = await client.FetchAsync(url!, request.Id, category, ct);
            // Deliberately project: GUIDs, descriptions and all links may contain secrets.
            return Ok(new { ok = true, itemsCount = entries.Count, sample = entries.Take(5).Select(e => new
                { e.Item.Title, e.Item.PublishedAtTs, e.Item.SizeBytes, e.Item.Seeders, e.Item.Leechers }) });
        }
        catch (DirectRssException ex) { return Ok(new { ok = false, error = ErrorMessageSanitizer.ToOperationalMessage(ex, "RSS test failed") }); }
    }
}

public sealed class RssSourceRequest
{
    [Required, StringLength(200, MinimumLength = 1)] public string Name { get; set; } = "";
    [RegularExpression("^#[0-9a-fA-F]{6}$")] public string? Color { get; set; }
    [Required] public List<RssFeedRequest> Feeds { get; set; } = new();
}
public class RssFeedRequest
{
    public long Id { get; set; }
    [StringLength(8192)] public string? Url { get; set; }
    [StringLength(200)] public string? Name { get; set; }
    [Required] public string CategoryKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
public sealed class RssFeedTestRequest : RssFeedRequest
{
    public long? SourceId { get; set; }
}
