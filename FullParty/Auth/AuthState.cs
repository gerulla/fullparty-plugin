namespace FullParty.Auth;

public enum AuthState
{
    SignedOut,
    Refreshing,
    RequestingDeviceCode,
    WaitingForApproval,
    VerifyingUser,
    ReadyToFinish,
    Authenticated,
    Error,
}
