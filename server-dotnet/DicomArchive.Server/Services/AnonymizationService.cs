using FellowOakDicom;

namespace DicomArchive.Server.Services;

public static class AnonymizationService
{
    // Tags replaced with "ANONYMOUS" (string VRs: LO, PN, SH)
    private static readonly DicomTag[] TagsToLabel =
    [
        DicomTag.PatientName,
        DicomTag.PatientID,
        DicomTag.PatientAddress,
        DicomTag.PatientTelephoneNumbers,
        DicomTag.OtherPatientIDsRETIRED,
        DicomTag.OtherPatientNames,
        DicomTag.ReferringPhysicianName,
        DicomTag.InstitutionName,
        DicomTag.PerformingPhysicianName,
        DicomTag.OperatorsName,
    ];

    // Tags cleared to empty (typed VRs: DA, CS, AS, DS)
    private static readonly DicomTag[] TagsToClear =
    [
        DicomTag.PatientBirthDate,
        DicomTag.PatientSex,
        DicomTag.PatientAge,
        DicomTag.PatientWeight,
        DicomTag.AccessionNumber,
    ];

    private static readonly DicomTag[] TagsToRemove =
    [
        DicomTag.InstitutionAddress,
        DicomTag.StationName,
    ];

    public static void Anonymize(DicomDataset ds)
    {
        foreach (var tag in TagsToLabel)
        {
            if (ds.Contains(tag))
                ds.AddOrUpdate(tag, "ANONYMOUS");
        }

        foreach (var tag in TagsToClear)
        {
            if (ds.Contains(tag))
                ds.AddOrUpdate(tag, string.Empty);
        }

        foreach (var tag in TagsToRemove)
            ds.Remove(tag);
    }
}
