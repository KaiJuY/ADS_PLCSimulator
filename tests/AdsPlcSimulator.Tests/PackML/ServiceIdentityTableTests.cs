using AdsPlcSimulator.Core.PackML;

namespace AdsPlcSimulator.Tests.PackML;

public class ServiceIdentityTableTests
{
    private static long? Enums(string name) => name switch
    {
        "E_Id.Open" => 11,
        "E_Id.Close" => 12,
        "E_Id.Zero" => 0,
        "E_Behavior.Natural" => 1,
        "E_Behavior.External" => 2,
        _ => null,
    };

    private static ServiceIdentityTable Parse(string text) => ServiceIdentityTable.Parse(text, Enums);

    [Fact]
    [Trait("AC", "2")]
    public void Call_statement_gives_service_id_and_behavior()
    {
        ServiceIdentityTable table = Parse("F_Set(Status := ServiceStatus.Shutter.Open, ServiceId := E_Id.Open, CompletionBehavior := E_Behavior.Natural);");

        Assert.Equal(1, table.Count);
        Assert.Equal(new ServiceIdentity(11, CompletionBehavior.NaturalCompletion), table.Find("Shutter.Open"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Named_arguments_may_come_in_any_order()
    {
        ServiceIdentityTable table = Parse("F_Set(CompletionBehavior := E_Behavior.External, ServiceId := E_Id.Close, Status := ServiceStatus.Door);");

        Assert.Equal(new ServiceIdentity(12, CompletionBehavior.ExecuteUntilExternalStop), table.Find("Door"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Array_index_with_comma_stays_in_the_path()
    {
        ServiceIdentityTable table = Parse("F_Set(Status := ServiceStatus.Arm[1].Lift, ServiceId := E_Id.Open, CompletionBehavior := E_Behavior.Natural);");

        Assert.NotNull(table.Find("Arm[1].Lift"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Commas_inside_brackets_stay_in_the_path()
    {
        ServiceIdentityTable table = Parse("F_Set(Status := ServiceStatus.Grid[1,2].Lift, ServiceId := E_Id.Open, CompletionBehavior := E_Behavior.Natural);");

        Assert.Equal(1, table.Count);
        Assert.Equal(new ServiceIdentity(11, CompletionBehavior.NaturalCompletion), table.Find("Grid[1,2].Lift"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Argument_without_a_name_is_skipped()
    {
        ServiceIdentityTable table = Parse("F_Set(Status := ServiceStatus.Door, Spare, ServiceId := E_Id.Open, CompletionBehavior := E_Behavior.Natural);");

        Assert.Equal(new ServiceIdentity(11, CompletionBehavior.NaturalCompletion), table.Find("Door"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Comment_separates_tokens_like_white_space()
    {
        ServiceIdentityTable table = Parse("ServiceStatus.Door.CompletionBehavior := E_Behavior.(* split *)Natural;");

        Assert.Equal(0, table.Count);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Find_ignores_case_and_returns_null_for_unknown_path()
    {
        ServiceIdentityTable table = Parse("F_Set(Status := ServiceStatus.Shutter.Open, ServiceId := E_Id.Open, CompletionBehavior := E_Behavior.Natural);");

        Assert.NotNull(table.Find("SHUTTER.open"));
        Assert.Null(table.Find("Shutter.Close"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Comments_are_ignored()
    {
        ServiceIdentityTable table = Parse("""
            // F_Set(Status := ServiceStatus.Hidden, ServiceId := E_Id.Open, CompletionBehavior := E_Behavior.Natural);
            (* F_Set(Status := ServiceStatus.Hidden2, ServiceId := E_Id.Open,
               CompletionBehavior := E_Behavior.Natural); *)
            F_Set(Status := ServiceStatus.Shown, ServiceId := E_Id.Open, CompletionBehavior := E_Behavior.Natural); // trailing
            """);

        Assert.Equal(1, table.Count);
        Assert.NotNull(table.Find("Shown"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Behavior_assignment_alone_keeps_service_id_zero()
    {
        ServiceIdentityTable table = Parse("ServiceStatus.Spare.CompletionBehavior := E_Behavior.Natural;");

        Assert.Equal(new ServiceIdentity(0, CompletionBehavior.NaturalCompletion), table.Find("Spare"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Behavior_assignment_after_call_keeps_the_call_service_id()
    {
        ServiceIdentityTable table = Parse("""
            F_Set(Status := ServiceStatus.Door, ServiceId := E_Id.Close, CompletionBehavior := E_Behavior.Natural);
            ServiceStatus.Door.CompletionBehavior := E_Behavior.External;
            """);

        Assert.Equal(new ServiceIdentity(12, CompletionBehavior.ExecuteUntilExternalStop), table.Find("Door"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Later_call_overrides_earlier_call()
    {
        ServiceIdentityTable table = Parse("""
            F_Set(Status := ServiceStatus.Door, ServiceId := E_Id.Open, CompletionBehavior := E_Behavior.Natural);
            F_Set(Status := ServiceStatus.Door, ServiceId := E_Id.Close, CompletionBehavior := E_Behavior.External);
            """);

        Assert.Equal(1, table.Count);
        Assert.Equal(new ServiceIdentity(12, CompletionBehavior.ExecuteUntilExternalStop), table.Find("Door"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Unknown_enum_value_fails_with_its_name()
    {
        var exception = Assert.Throws<ServiceIdentityException>(
            () => Parse("F_Set(Status := ServiceStatus.Door, ServiceId := E_Id.Nope, CompletionBehavior := E_Behavior.Natural);"));

        Assert.Equal("Unknown enum value 'E_Id.Nope'.", exception.Message);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Unknown_enum_value_in_behavior_assignment_fails()
    {
        Assert.Throws<ServiceIdentityException>(() => Parse("ServiceStatus.Door.CompletionBehavior := E_Behavior.Nope;"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Other_statements_are_ignored()
    {
        ServiceIdentityTable table = Parse("""
            MEMSET(ADR(ServiceStatus), 0, SIZEOF(ServiceStatus));
            F_Init := TRUE;
            F_Set(Status := Other.Door, ServiceId := E_Id.Open, CompletionBehavior := E_Behavior.Natural);
            F_Set(Status := ServiceStatus.Door, ServiceId := E_Id.Open);
            """);

        Assert.Equal(0, table.Count);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Service_id_zero_is_kept_in_the_table()
    {
        ServiceIdentityTable table = Parse("F_Set(Status := ServiceStatus.Door, ServiceId := E_Id.Zero, CompletionBehavior := E_Behavior.Natural);");

        Assert.Equal(0u, table.Find("Door")!.ServiceId);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Xml_input_uses_the_first_st_element_and_skips_bom_and_blanks()
    {
        string xml = "﻿  \r\n<TcPlcObject><POU><Implementation><ST><![CDATA[F_Set(Status := ServiceStatus.Door, ServiceId := E_Id.Open, CompletionBehavior := E_Behavior.Natural);]]></ST></Implementation>"
            + "<Method><Implementation><ST><![CDATA[F_Set(Status := ServiceStatus.Other, ServiceId := E_Id.Open, CompletionBehavior := E_Behavior.Natural);]]></ST></Implementation></Method></POU></TcPlcObject>";

        ServiceIdentityTable table = Parse(xml);

        Assert.Equal(1, table.Count);
        Assert.NotNull(table.Find("Door"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Xml_without_st_element_fails()
    {
        var exception = Assert.Throws<ServiceIdentityException>(() => Parse("<TcPlcObject><POU /></TcPlcObject>"));

        Assert.Equal("The XML has no <ST> element.", exception.Message);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Malformed_xml_fails()
    {
        var exception = Assert.Throws<ServiceIdentityException>(() => Parse("<TcPlcObject><POU>"));

        Assert.StartsWith("The identity file is not valid XML: ", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n ")]
    [Trait("AC", "2")]
    public void Empty_input_gives_an_empty_table(string text)
    {
        Assert.Equal(0, Parse(text).Count);
    }
}
