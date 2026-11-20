// State enums mirror ALS-Community's ALSCharacterEnumLibrary.h (MIT, see THIRD_PARTY_NOTICES.md).

namespace ALSUnity
{
    public enum ALSMovementState
    {
        None,
        Grounded,
        InAir,
        Mantling,
        Ragdoll
    }

    public enum ALSMovementAction
    {
        None,
        LowMantle,
        HighMantle,
        Rolling,
        GettingUp
    }

    public enum ALSRotationMode
    {
        VelocityDirection,
        LookingDirection,
        Aiming
    }

    public enum ALSGait
    {
        Walking,
        Running,
        Sprinting
    }

    public enum ALSStance
    {
        Standing,
        Crouching
    }

    public enum ALSMovementDirection
    {
        Forward,
        Right,
        Left,
        Backward
    }

    public enum ALSMantleType
    {
        HighMantle,
        LowMantle,
        FallingCatch
    }
}
