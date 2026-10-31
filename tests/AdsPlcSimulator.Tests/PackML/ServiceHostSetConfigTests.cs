using AdsPlcSimulator.Core.PackML;

namespace AdsPlcSimulator.Tests.PackML;

public class ServiceHostSetConfigTests
{
    private const string Module = "GVL_Test.Modules[1]";
    private const string Batch = "GVL_Test.Modules[1].ServiceParam.A";
    private const string ErrorList = "GVL_Test.Modules[1].ServiceStatus.A.ErrorList[1]";

    private const int Integer32 = 2;
    private const int Float32 = 4;
    private const int String80 = 6;

    private const string ConfigTypes = """
        TYPE E_ConfigDataType : (Unknown := 0, Boolean := 1, Integer32 := 2, UnsignedInteger32 := 3, Float32 := 4, Float64 := 5,
            String80 := 6, Integer8 := 7, UnsignedInteger8 := 8, Integer16 := 9, UnsignedInteger16 := 10) UDINT; END_TYPE
        TYPE E_DescriptorType : (Integer_32bit := 1, Float_32bit := 2, String_80byte := 3, DateTime := 4, Boolean := 5,
            Unsigned_32bit := 6, Float_64bit := 7, Integer_8bit := 8, Unsigned_8bit := 9, Integer_16bit := 10, Unsigned_16bit := 11) UDINT; END_TYPE
        TYPE ST_SetConfigParamParam : STRUCT Id : UDINT; DataType : E_ConfigDataType; Value : STRING(80); END_STRUCT END_TYPE
        TYPE T_SetConfigParamList : ARRAY[1..5] OF ST_SetConfigParamParam; END_TYPE
        TYPE ST_OldParam : STRUCT Name : STRING(30); Value : STRING(80); END_STRUCT END_TYPE
        TYPE ST_ConfigParamDescriptor : STRUCT
            Id : UDINT;
            Name : STRING(30);
            DataType : E_DescriptorType;
            Unit : STRING(10);
            Value : STRING(80);
            IsBound : BOOL;
            HasLimits : BOOL;
            LowerLimit : LREAL;
            UpperLimit : LREAL;
        END_STRUCT END_TYPE
        """;

    private static string Declarations(string paramA = "T_SetConfigParamList", bool withMetadata = true)
    {
        string text = HostFixture.AlarmDeclarations
            .Replace("A : ST_TestParam;", $"A : {paramA};");
        if (withMetadata)
            text = text.Replace("TYPE ST_TestModule : STRUCT", "TYPE ST_TestModule : STRUCT ModuleConfigParamList : ARRAY[1..5] OF ST_ConfigParamDescriptor;");
        return ConfigTypes + Environment.NewLine + text;
    }

    private static HostFixture Create(string? declarations = null)
    {
        var fixture = new HostFixture(declarations: declarations ?? Declarations());
        Descriptor(fixture, 1, 101, 1, true, 0, 100, "5");
        Descriptor(fixture, 2, 102, 2, true, -1000, 1000, "1.5");
        Descriptor(fixture, 3, 103, 3, true, 0, 0, "old");
        Descriptor(fixture, 4, 104, 1, false, 0, 100, "9");
        return fixture;
    }

    private static void Descriptor(HostFixture fixture, int index, long id, long descriptorType, bool bound, double lower, double upper, string value)
    {
        string path = $"{Module}.ModuleConfigParamList[{index}]";
        fixture.Write($"{path}.Id", id);
        fixture.Write($"{path}.DataType", descriptorType);
        fixture.Write($"{path}.IsBound", bound);
        fixture.Write($"{path}.LowerLimit", lower);
        fixture.Write($"{path}.UpperLimit", upper);
        fixture.Write($"{path}.Value", value);
    }

    private static void Item(HostFixture fixture, int index, long id, long dataType, string value)
    {
        fixture.Write($"{Batch}[{index}].Id", id);
        fixture.Write($"{Batch}[{index}].DataType", dataType);
        fixture.Write($"{Batch}[{index}].Value", value);
    }

    private static string ValueOf(HostFixture fixture, int index) =>
        (string)fixture.Read($"{Module}.ModuleConfigParamList[{index}].Value");

    private static string[] Values(HostFixture fixture) => [ValueOf(fixture, 1), ValueOf(fixture, 2), ValueOf(fixture, 3)];

    private static readonly string[] OriginalValues = ["5", "1.5", "old"];

    private static long Number(HostFixture fixture, string path) => Convert.ToInt64(fixture.Read(path));

    private static PackMLState State(HostFixture fixture) => fixture.ServiceState(1, "A");

    private static void StartA(HostFixture fixture)
    {
        fixture.SendToService(1, "A", PackMLCommand.Start);
        fixture.Scan();
    }

    private static void ReleaseAndScan(HostFixture fixture)
    {
        fixture.ReleaseService(1, "A");
        fixture.Scan();
    }

    [Fact]
    [Trait("AC", "5")]
    public void Valid_batch_enters_Execute_in_the_Start_scan_writes_Values_and_completes_after_two_more_scans()
    {
        HostFixture fixture = Create();
        Item(fixture, 1, 101, Integer32, "+042");
        Item(fixture, 2, 102, Float32, "2.5");
        Item(fixture, 3, 103, String80, "hello");

        StartA(fixture);

        Assert.Equal(PackMLState.Execute, State(fixture));
        Assert.Equal(["42", "2.5", "hello"], Values(fixture));
        ReleaseAndScan(fixture);
        Assert.Equal(PackMLState.Completing, State(fixture));
        fixture.Scan();
        Assert.Equal(PackMLState.Complete, State(fixture));
        Assert.Equal(0, Number(fixture, $"{ErrorList}.MainErrorId"));
        Assert.Equal("9", ValueOf(fixture, 4));
    }

    [Theory]
    [Trait("AC", "3")]
    [InlineData(920, 0, "Config Param batch is empty.")]
    [InlineData(921, 101, "Config Param ID is duplicated in the batch.")]
    [InlineData(922, 999, "Config Param metadata was not found.")]
    [InlineData(923, 104, "Config Param is not bound to Persistent storage.")]
    [InlineData(924, 101, "Config Param dataType does not match metadata.")]
    [InlineData(925, 101, "Config Param value has an invalid strict format.")]
    [InlineData(926, 101, "Config Param value is outside its type or metadata limits.")]
    public void Each_batch_error_stays_in_Starting_one_scan_then_aborts_with_the_error(int errorId, int paramId, string message)
    {
        HostFixture fixture = Create();
        PrepareFailingBatch(fixture, errorId);

        StartA(fixture);

        Assert.Equal(PackMLState.Starting, State(fixture));
        ReleaseAndScan(fixture);
        Assert.Equal(PackMLState.Aborted, State(fixture));
        Assert.Equal(errorId, Number(fixture, $"{ErrorList}.MainErrorId"));
        Assert.Equal(paramId, Number(fixture, $"{ErrorList}.SourceErrorId"));
        Assert.Equal(message, fixture.Read($"{ErrorList}.Message"));
        Assert.Equal(OriginalValues, Values(fixture));
    }

    private static void PrepareFailingBatch(HostFixture fixture, int errorId)
    {
        switch (errorId)
        {
            case 921:
                Item(fixture, 1, 101, Integer32, "1");
                Item(fixture, 2, 101, Integer32, "2");
                break;
            case 922:
                Item(fixture, 1, 999, Integer32, "1");
                break;
            case 923:
                Item(fixture, 1, 104, Integer32, "1");
                break;
            case 924:
                Item(fixture, 1, 101, Float32, "1");
                break;
            case 925:
                Item(fixture, 1, 101, Integer32, "abc");
                break;
            case 926:
                Item(fixture, 1, 101, Integer32, "101");
                break;
        }
    }

    [Fact]
    [Trait("AC", "3")]
    public void A_later_failing_item_stops_the_valid_first_item_from_being_written()
    {
        HostFixture fixture = Create();
        Item(fixture, 1, 101, Integer32, "50");
        Item(fixture, 2, 102, Float32, "2000");

        StartA(fixture);
        ReleaseAndScan(fixture);

        Assert.Equal(PackMLState.Aborted, State(fixture));
        Assert.Equal(926, Number(fixture, $"{ErrorList}.MainErrorId"));
        Assert.Equal(102, Number(fixture, $"{ErrorList}.SourceErrorId"));
        Assert.Equal(OriginalValues, Values(fixture));
    }

    [Theory]
    [Trait("AC", "3")]
    [InlineData(101, Integer32, "2147483648")]
    [InlineData(102, Float32, "1e39")]
    public void A_value_outside_the_data_type_range_is_out_of_range(long id, long dataType, string value)
    {
        HostFixture fixture = Create();
        Item(fixture, 1, id, dataType, value);

        StartA(fixture);
        ReleaseAndScan(fixture);

        Assert.Equal(PackMLState.Aborted, State(fixture));
        Assert.Equal(926, Number(fixture, $"{ErrorList}.MainErrorId"));
        Assert.Equal(id, Number(fixture, $"{ErrorList}.SourceErrorId"));
        Assert.Equal(OriginalValues, Values(fixture));
    }

    [Fact]
    [Trait("AC", "3")]
    public void A_float_that_overflows_to_infinity_is_an_invalid_format_not_out_of_range()
    {
        HostFixture fixture = Create();
        Item(fixture, 1, 102, Float32, "1e999");

        StartA(fixture);
        ReleaseAndScan(fixture);

        Assert.Equal(PackMLState.Aborted, State(fixture));
        Assert.Equal(925, Number(fixture, $"{ErrorList}.MainErrorId"));
        Assert.Equal(102, Number(fixture, $"{ErrorList}.SourceErrorId"));
        Assert.Equal(OriginalValues, Values(fixture));
    }

    [Theory]
    [Trait("AC", "3")]
    [InlineData("101", false)]
    [InlineData("100", true)]
    [InlineData("0", true)]
    [InlineData("-1", false)]
    public void Metadata_limits_are_inclusive(string value, bool passes)
    {
        HostFixture fixture = Create();
        Item(fixture, 1, 101, Integer32, value);

        StartA(fixture);

        Assert.Equal(passes ? PackMLState.Execute : PackMLState.Starting, State(fixture));
        Assert.Equal(passes ? value : "5", ValueOf(fixture, 1));
    }

    [Fact]
    [Trait("AC", "3")]
    public void Limits_are_compared_even_when_HasLimits_is_false()
    {
        HostFixture fixture = Create();
        fixture.Write($"{Module}.ModuleConfigParamList[1].HasLimits", false);
        Item(fixture, 1, 101, Integer32, "101");

        StartA(fixture);

        Assert.Equal(PackMLState.Starting, State(fixture));
    }

    [Theory]
    [Trait("AC", "3")]
    [InlineData(0)]
    [InlineData(11)]
    public void Batch_DataType_outside_the_known_values_is_a_mismatch(long dataType)
    {
        HostFixture fixture = Create();
        Item(fixture, 1, 101, dataType, "1");

        StartA(fixture);
        ReleaseAndScan(fixture);

        Assert.Equal(PackMLState.Aborted, State(fixture));
        Assert.Equal(924, Number(fixture, $"{ErrorList}.MainErrorId"));
    }

    [Fact]
    [Trait("AC", "3")]
    public void Mode_request_high_keeps_the_service_in_Starting_until_released_then_aborts()
    {
        HostFixture fixture = Create();
        Item(fixture, 1, 101, Integer32, "101");
        fixture.Write("GVL_Test.Modules[1].ServiceCtrl.A.PackMLIn.eMode", 1L);
        fixture.Write("GVL_Test.Modules[1].ServiceCtrl.A.PackMLIn.bModeChangeRequest", true);

        StartA(fixture);
        ReleaseAndScan(fixture);
        fixture.Scan();

        Assert.Equal(PackMLState.Starting, State(fixture));
        Assert.Equal(926, Number(fixture, $"{ErrorList}.MainErrorId"));

        fixture.Write("GVL_Test.Modules[1].ServiceCtrl.A.PackMLIn.bModeChangeRequest", false);
        fixture.Scan();
        Assert.Equal(PackMLState.Starting, State(fixture));
        fixture.Scan();
        Assert.Equal(PackMLState.Aborted, State(fixture));
        Assert.Equal(OriginalValues, Values(fixture));
    }

    [Fact]
    [Trait("AC", "3")]
    public void Client_Abort_or_Stop_leaves_a_stuck_Starting_without_writing()
    {
        HostFixture abort = StuckInStarting();
        abort.ServiceCommand(1, "A", PackMLCommand.Abort, 2);
        Assert.Equal(PackMLState.Aborted, State(abort));
        Assert.Equal(OriginalValues, Values(abort));

        HostFixture stop = StuckInStarting();
        stop.ServiceCommand(1, "A", PackMLCommand.Stop, 2);
        Assert.Equal(PackMLState.Stopped, State(stop));
        Assert.Equal(OriginalValues, Values(stop));
    }

    [Fact]
    [Trait("AC", "3")]
    public void After_Abort_of_a_stuck_Starting_Clear_Reset_and_a_valid_batch_succeeds()
    {
        HostFixture fixture = StuckInStarting();
        fixture.ServiceCommand(1, "A", PackMLCommand.Abort, 2);
        fixture.Write("GVL_Test.Modules[1].ServiceCtrl.A.PackMLIn.bModeChangeRequest", false);
        fixture.ServiceCommand(1, "A", PackMLCommand.Clear, 3);
        fixture.ServiceCommand(1, "A", PackMLCommand.Reset, 4);
        Assert.Equal(PackMLState.Idle, State(fixture));
        Item(fixture, 1, 101, Integer32, "77");

        fixture.SendToService(1, "A", PackMLCommand.Start, 5);
        fixture.Scan();

        Assert.Equal(PackMLState.Execute, State(fixture));
        Assert.Equal("77", ValueOf(fixture, 1));
    }

    private static HostFixture StuckInStarting()
    {
        HostFixture fixture = Create();
        Item(fixture, 1, 101, Integer32, "101");
        fixture.Write("GVL_Test.Modules[1].ServiceCtrl.A.PackMLIn.eMode", 1L);
        fixture.Write("GVL_Test.Modules[1].ServiceCtrl.A.PackMLIn.bModeChangeRequest", true);
        StartA(fixture);
        ReleaseAndScan(fixture);
        fixture.Scan();
        Assert.Equal(PackMLState.Starting, State(fixture));
        return fixture;
    }

    [Fact]
    [Trait("AC", "3")]
    public void After_a_failed_batch_Clear_Reset_and_a_valid_batch_succeeds()
    {
        HostFixture fixture = Create();
        Item(fixture, 1, 101, Integer32, "101");
        StartA(fixture);
        ReleaseAndScan(fixture);
        Assert.Equal(PackMLState.Aborted, State(fixture));

        fixture.ServiceCommand(1, "A", PackMLCommand.Clear, 2);
        fixture.ServiceCommand(1, "A", PackMLCommand.Reset, 3);
        Assert.Equal(PackMLState.Idle, State(fixture));
        Assert.Equal("5", ValueOf(fixture, 1));
        Item(fixture, 1, 101, Integer32, "60");
        fixture.SendToService(1, "A", PackMLCommand.Start, 4);
        fixture.Scan();

        Assert.Equal(PackMLState.Execute, State(fixture));
        Assert.Equal("60", ValueOf(fixture, 1));
    }

    [Fact]
    [Trait("AC", "5")]
    public void Unhold_back_to_Execute_does_not_write_the_batch_again()
    {
        HostFixture fixture = Create();
        fixture.SetOverrideToken(1, "A", new object());
        Item(fixture, 1, 101, Integer32, "70");
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 1);
        Assert.Equal(PackMLState.Execute, State(fixture));
        Assert.Equal("70", ValueOf(fixture, 1));

        fixture.Write($"{Module}.ModuleConfigParamList[1].Value", "7");
        fixture.ServiceCommand(1, "A", PackMLCommand.Hold, 2);
        Assert.Equal(PackMLState.Held, State(fixture));
        fixture.ServiceCommand(1, "A", PackMLCommand.Unhold, 3);

        Assert.Equal(PackMLState.Execute, State(fixture));
        Assert.Equal("7", ValueOf(fixture, 1));
    }

    [Fact]
    [Trait("AC", "5")]
    public void A_workflow_that_took_over_the_service_still_gets_the_Values_written_on_entering_Execute()
    {
        HostFixture fixture = Create();
        fixture.SetOverrideToken(1, "A", new object());
        Item(fixture, 1, 101, Integer32, "70");

        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 1);
        fixture.Scan(TimeSpan.FromSeconds(10));

        Assert.Equal(PackMLState.Execute, State(fixture));
        Assert.Equal("70", ValueOf(fixture, 1));
    }

    [Fact]
    [Trait("AC", "6")]
    public void A_param_that_is_not_a_config_list_keeps_the_timed_completion()
    {
        HostFixture fixture = Create(Declarations(paramA: "ST_TestParam"));

        AssertTimedCompletion(fixture);
    }

    [Fact]
    [Trait("AC", "6")]
    public void The_old_Name_Value_param_shape_keeps_the_timed_completion()
    {
        HostFixture fixture = Create(Declarations(paramA: "ARRAY[1..4] OF ST_OldParam"));

        AssertTimedCompletion(fixture);
    }

    [Fact]
    [Trait("AC", "6")]
    public void A_module_without_ModuleConfigParamList_keeps_the_timed_completion()
    {
        var fixture = new HostFixture(declarations: Declarations(withMetadata: false));

        AssertTimedCompletion(fixture);
    }

    private static void AssertTimedCompletion(HostFixture fixture)
    {
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 1);
        Assert.Equal(PackMLState.Execute, State(fixture));

        fixture.Scan(HostFixture.DefaultDelay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(PackMLState.Execute, State(fixture));
        fixture.Scan(TimeSpan.FromMilliseconds(1));
        Assert.Equal(PackMLState.Completing, State(fixture));
        fixture.Scan();
        Assert.Equal(PackMLState.Complete, State(fixture));
    }
}
