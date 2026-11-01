using LightView.Core.Settings;

namespace LightView.App.Imaging;

internal static class DetailsProjection
{
    public static IReadOnlyList<ImageDetailSection> Apply(IReadOnlyList<ImageDetailSection> source, DetailsLayout? layout)
    {
        var configured = DetailsCatalog.Normalize(layout); var result = new List<ImageDetailSection>();
        foreach(var group in configured.Groups.Where(g=>g.Enabled))
        {
            var actual = source.FirstOrDefault(section=>section.Id==group.Id);
            if(actual is null) continue;
            var known = DetailsCatalog.Groups.FirstOrDefault(g=>g.Id==group.Id);
            if (known is null) continue;
            var rows = group.Fields.Where(f=>f.Enabled && known.Fields.Any(field=>field.Id==f.Id)).Select(f=>actual.Rows.FirstOrDefault(row=>row.Id==f.Id && !DetailsCatalog.IsRetired(row.Id))).Where(row=>row is not null).Cast<ImageDetail>().ToList();
            rows.AddRange(actual.Rows.Where(row=>row.Id is null)); // Legacy rows have no configurable identity.
            if(rows.Count>0) result.Add(actual with { Rows=rows.ToArray() });
        }
        // Required metadata notices remain independent of ordinary configurable fields.
        result.AddRange(source.Where(section=>!DetailsCatalog.Groups.Any(group=>group.Id==section.Id)));
        return result;
    }
}
