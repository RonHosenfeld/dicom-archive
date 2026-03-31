using FellowOakDicom;

namespace DicomArchive.Server.Services;

/// <summary>
/// Applies prefix coercion (add/remove) to AccessionNumber and PatientID
/// when routing DICOM instances to a destination.
/// </summary>
public static class CoercionService
{
    private static readonly DicomTag[] CoercedTags =
    [
        DicomTag.AccessionNumber,
        DicomTag.PatientID,
    ];

    public static void Apply(DicomDataset dataset, string action, string prefix)
    {
        var wasAutoValidate = dataset.AutoValidate;
        dataset.AutoValidate = false;
        try
        {
            foreach (var tag in CoercedTags)
            {
                var value = dataset.GetSingleValueOrDefault(tag, "");
                var newValue = action switch
                {
                    "add"    => value.StartsWith(prefix, StringComparison.Ordinal) ? value : prefix + value,
                    "remove" => value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value,
                    _        => value,
                };

                if (newValue != value)
                    dataset.AddOrUpdate(tag, newValue);
            }
        }
        finally
        {
            dataset.AutoValidate = wasAutoValidate;
        }
    }
}
