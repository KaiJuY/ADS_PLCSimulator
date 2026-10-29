(*
    Interface-only copy of FB_Chamber1_Module. The real FB's internal service instances
    are replaced by workflows, so only the inputs and outputs are kept.
*)
FUNCTION_BLOCK FB_Chamber1_Module
VAR_INPUT
    HwInput : ST_Chamber1_Signal_In;
    ServiceCtrl : REFERENCE TO ST_Chamber1_ServiceCtrl;
    ServiceParam : REFERENCE TO ST_Chamber1_ServiceParam;
    SpinAxis_BaseUnit : I_Axis_BaseUnit;
    LiftPinAxis_BaseUnit : I_Axis_BaseUnit;
END_VAR
VAR_OUTPUT
    HwOutput : ST_Chamber1_Signal_Out;
    ServiceStatus : ST_Chamber1_ServiceStatus;
END_VAR
END_FUNCTION_BLOCK
