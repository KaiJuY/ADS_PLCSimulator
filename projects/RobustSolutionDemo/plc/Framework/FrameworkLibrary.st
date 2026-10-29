(*
    Stand-ins for types that come from the compiled framework library.
    PackML and ServiceCtrl types match the library layout exactly.
    Large status types keep only the members the simulator workflows use.
*)

TYPE E_PackMLCommand :
(
    None := 0, Reset := 1, Start := 2, Stop := 3, Hold := 4,
    Unhold := 5, Suspend := 6, Unsuspend := 7, Abort := 8, Clear := 9
) INT;
END_TYPE

TYPE E_PackMLState :
(
    Undefined := 0, Clearing := 1, Stopped := 2, Starting := 3, Idle := 4, Suspended := 5,
    Execute := 6, Stopping := 7, Aborting := 8, Aborted := 9, Holding := 10, Held := 11,
    UnHolding := 12, Suspending := 13, Unsuspending := 14, Resetting := 15, Completing := 16, Complete := 17
) INT;
END_TYPE

TYPE T_PackMLMode : UINT; END_TYPE

TYPE E_PackMLErrorID : (NoError := 0) UDINT; END_TYPE

TYPE E_ServiceCompletionBehavior :
(
    Unknown := 0,
    NaturalCompletion := 1,
    ExecuteUntilExternalStop := 2
) UDINT;
END_TYPE

TYPE ST_PML_Base_In :
STRUCT
    eCommand : E_PackMLCommand;
    bCommandChangeRequest : BOOL;
    eMode : T_PackMLMode;
    bModeChangeRequest : BOOL;
END_STRUCT
END_TYPE

TYPE ST_PML_Base_Out :
STRUCT
    eStateCurrent : E_PackMLState;
    eStatePrevious : E_PackMLState;
    eModeCurrent : T_PackMLMode;
    eModePrevious : T_PackMLMode;
    bCommandAccepted : BOOL;
    bCommandRejected : BOOL;
    eStateErrorID : E_PackMLErrorID;
    bModeChanged : BOOL;
    bModeChangeRejected : BOOL;
    eModeErrorID : E_PackMLErrorID;
END_STRUCT
END_TYPE

TYPE ST_ServiceCtrl :
STRUCT
    RequestId : UDINT;
    PackMLIn : ST_PML_Base_In;
END_STRUCT
END_TYPE

(* Library version also carries TimeCollection, ErrorList and Data. *)
TYPE ST_ServiceStatus :
STRUCT
    ResponseId : UDINT;
    PackMLOut : ST_PML_Base_Out;
    CompletionBehavior : E_ServiceCompletionBehavior;
    ServiceId : UDINT;
END_STRUCT
END_TYPE

TYPE ST_EmptyServiceParam :
STRUCT
    bDummyValue : BOOL;
END_STRUCT
END_TYPE

TYPE ST_MaterialParam :
STRUCT
    CmdType : INT;
    TargetEndpointId : UDINT;
    MaterialId : STRING(80);
    MaterialOperateState : INT;
END_STRUCT
END_TYPE

TYPE ST_SetConfigParamParam :
STRUCT
    Name : STRING(80);
    Value : STRING(80);
END_STRUCT
END_TYPE

TYPE T_SetConfigParamList : ARRAY[1..4] OF ST_SetConfigParamParam; END_TYPE

TYPE ST_ModuleCtrl :
STRUCT
    RequestId : UDINT;
    PackMLIn : ST_PML_Base_In;
END_STRUCT
END_TYPE

TYPE ST_ModuleStatus :
STRUCT
    ModuleId : UDINT;
    Enabled : BOOL;
    IsControlSourceAlive : BOOL;
    ModuleType : STRING(16);
    ResponseId : UDINT;
    PackMLOut : ST_PML_Base_Out;
END_STRUCT
END_TYPE

(* Library version also carries variable, alarm, data and config descriptor lists. *)
TYPE ST_Module_CtrlStatus_Base :
STRUCT
    HeartbeatIndex : UDINT;
    ModuleCtrl : ST_ModuleCtrl;
    ModuleStatus : ST_ModuleStatus;
END_STRUCT
END_TYPE

TYPE T_ModuleTypeName : STRING(16); END_TYPE

TYPE ST_ModuleRuntimeConfig :
STRUCT
    Enabled : BOOL := TRUE;
    Valid : BOOL;
    ModuleId : UDINT;
    ModuleType : T_ModuleTypeName := 'Chamber1';
    Revision : UDINT;
END_STRUCT
END_TYPE

INTERFACE I_Axis_BaseUnit
END_INTERFACE
