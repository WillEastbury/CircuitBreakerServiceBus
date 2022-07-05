namespace CircuitBreaker
{
    public enum CircuitState : int 
    { 
        Dead,
        InTrouble,
        OK
    }
    public enum RequestStatusType
    {
        Success,
        Failure,
        TransientFailure
    }
}