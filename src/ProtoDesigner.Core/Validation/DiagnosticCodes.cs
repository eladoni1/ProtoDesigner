namespace ProtoDesigner.Core.Validation;

/// <summary>
/// Stable diagnostic codes. Never renumber, never repurpose — once shipped a code means what it meant
/// on the day it shipped. Add new codes; retire old ones by leaving the constant in place and marking
/// the rule dead.
/// </summary>
public static class DiagnosticCodes
{
    // ---- structural: names and IDs (PD0001..PD0009) ----------------------------------------------

    public const string DuplicateFieldName        = "PD0001";
    public const string DuplicateMessageName      = "PD0002";
    public const string DuplicateBusName          = "PD0003";
    public const string DuplicateWireId           = "PD0004";
    public const string DuplicateTypeName         = "PD0005";
    public const string InvalidName               = "PD0006";
    public const string RouteUnknownModule        = "PD0007";
    public const string DuplicateRoute            = "PD0008";
    public const string RouteToSelf               = "PD0009";

    // ---- type graph (PD0010..PD0019) --------------------------------------------------------------

    public const string RecursiveType             = "PD0010";
    public const string UnknownTypeReference      = "PD0011";
    public const string EnumWithoutMembers        = "PD0012";
    public const string DuplicateEnumMember       = "PD0013";

    // ---- encoding feasibility (PD0020..PD0029) ----------------------------------------------------

    public const string WidthTooSmallForRange     = "PD0020";
    public const string EnumMemberDoesNotFit      = "PD0021";
    public const string DefaultValueOutOfRange    = "PD0022";
    public const string InvalidBitWidth           = "PD0023";
    public const string InvalidAlignment          = "PD0024";

    // ---- dynamic arrays (PD0030..PD0039) ---------------------------------------------------------

    public const string CountFieldMissing         = "PD0030";
    public const string CountFieldAfterArray      = "PD0031";
    public const string CountFieldNotInteger      = "PD0032";
    public const string CountFieldTooNarrow       = "PD0033";
    public const string LengthPrefixTooNarrow     = "PD0034";
    public const string DynamicArrayInsideDynamic = "PD0035";
    public const string ArrayOfCompositeElement   = "PD0036";

    // ---- CRC (PD0040..PD0049) ---------------------------------------------------------------------

    public const string CrcCoversItself           = "PD0040";
    public const string CrcCoverageOutOfOrder     = "PD0041";

    // ---- transport budget (PD0050..PD0059) --------------------------------------------------------

    public const string MtuExceeded               = "PD0050";
    public const string MtuAtRisk                 = "PD0051";

    // ---- housekeeping (PD0060..PD0069) ------------------------------------------------------------

    public const string UnreferencedType          = "PD0060";
    public const string BusHasNoMessages          = "PD0061";
    public const string MessageHasNoFields        = "PD0062";
    public const string WireIdNotAssigned         = "PD0063";
}
