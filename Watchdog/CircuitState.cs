namespace CircuitBreaker.Config
{
    public enum CircuitState : int 
    { 
        Dead,
        InTrouble,
        OK
    }
}