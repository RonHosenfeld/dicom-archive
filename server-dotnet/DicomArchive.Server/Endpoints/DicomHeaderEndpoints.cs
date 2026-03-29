using DicomArchive.Server.Data;
using DicomArchive.Server.Services;
using FellowOakDicom;
using Microsoft.EntityFrameworkCore;

namespace DicomArchive.Server.Endpoints;

public static class DicomHeaderEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/instances/{instanceUid}/dicom-header", GetDicomHeader);
    }

    static async Task<IResult> GetDicomHeader(
        ArchiveDbContext db, StorageService storage, string instanceUid)
    {
        var inst = await db.Instances.FirstOrDefaultAsync(i => i.InstanceUid == instanceUid);
        if (inst is null) return Results.NotFound();

        string? path = null;
        try
        {
            path = await storage.FetchToTempAsync(inst.BlobKey);
            var dicomFile = await DicomFile.OpenAsync(path, FileReadOption.SkipLargeTags);

            var meta = ExtractTags(dicomFile.FileMetaInfo);
            var dataset = ExtractTags(dicomFile.Dataset);

            return Results.Ok(new { meta, dataset });
        }
        finally
        {
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
    }

    private static List<TagEntry> ExtractTags(DicomDataset ds)
    {
        var result = new List<TagEntry>();
        foreach (var item in ds)
        {
            var tag = item.Tag;
            var tagStr = $"({tag.Group:X4},{tag.Element:X4})";
            var vr = item.ValueRepresentation.Code;
            var keyword = tag.DictionaryEntry.Keyword;
            var name = tag.DictionaryEntry.Name;

            if (item is DicomSequence seq)
            {
                var items = new List<List<TagEntry>>();
                foreach (var seqItem in seq.Items)
                    items.Add(ExtractTags(seqItem));

                result.Add(new TagEntry(tagStr, vr, keyword, name, $"({seq.Items.Count} item{(seq.Items.Count != 1 ? "s" : "")})", items));
            }
            else if (tag == DicomTag.PixelData)
            {
                long byteCount = 0;
                if (item is DicomFragmentSequence frag)
                {
                    foreach (var fragment in frag)
                        byteCount += fragment.Size;
                }
                else if (item is DicomElement elem)
                {
                    byteCount = elem.Buffer?.Size ?? 0;
                }
                result.Add(new TagEntry(tagStr, vr, keyword, name, $"[Pixel Data: {byteCount:N0} bytes]", null));
            }
            else
            {
                var value = "";
                try
                {
                    value = string.Join("\\", ds.GetValues<string>(tag));
                }
                catch
                {
                    try { value = item.ToString(); } catch { value = "[unable to read]"; }
                }
                if (value.Length > 512)
                    value = value[..512] + "…";
                result.Add(new TagEntry(tagStr, vr, keyword, name, value, null));
            }
        }
        return result;
    }

    private record TagEntry(string Tag, string Vr, string Keyword, string Name, string Value, List<List<TagEntry>>? Items);
}
