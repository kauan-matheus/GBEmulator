namespace EmuladorGameboy.Core;

/// <summary>
/// Os registradores internos da CPU Sharp LR35902.
///
/// São 7 registradores de 8 bits "comuns" (A,B,C,D,E,H,L) + o F (flags), que se
/// combinam em 4 pares de 16 bits (AF, BC, DE, HL). Mais SP (topo da pilha) e
/// PC (endereço da próxima instrução).
///
///   A  F        AF      A = acumulador; F = flags (Z N H C)
///   B  C        BC
///   D  E        DE
///   H  L        HL      (costuma guardar um ENDEREÇO)
///   SP          (16 bits)
///   PC          (16 bits)
/// </summary>
internal sealed class Registers
{
    public byte A;
    public byte B, C, D, E, H, L;

    // F é especial: SÓ os 4 bits altos (Z,N,H,C) existem no hardware; os 4 bits
    // baixos são sempre 0. Por isso o setter mascara com 0xF0 — escrever um valor
    // "sujo" nos bits baixos simplesmente não gruda.
    private byte _f;
    public byte F
    {
        get => _f;
        set => _f = (byte)(value & 0xF0);
    }

    public ushort SP; // Stack Pointer
    public ushort PC; // Program Counter

    // --- Pares de 16 bits ---
    // O registrador "de cima" é o byte mais significativo (high).
    public ushort AF
    {
        get => (ushort)((A << 8) | _f);
        set { A = (byte)(value >> 8); F = (byte)value; }
    }
    public ushort BC
    {
        get => (ushort)((B << 8) | C);
        set { B = (byte)(value >> 8); C = (byte)value; }
    }
    public ushort DE
    {
        get => (ushort)((D << 8) | E);
        set { D = (byte)(value >> 8); E = (byte)value; }
    }
    public ushort HL
    {
        get => (ushort)((H << 8) | L);
        set { H = (byte)(value >> 8); L = (byte)value; }
    }

    // --- Flags individuais (bits do registrador F) ---
    //   Z = bit 7 (resultado deu zero?)
    //   N = bit 6 (a última operação foi subtração?)
    //   H = bit 5 (half-carry: "vazou" do bit 3 pro 4?)
    //   C = bit 4 (carry: "vazou" do bit 7?)
    public bool FlagZ { get => (_f & 0x80) != 0; set => SetFlag(0x80, value); }
    public bool FlagN { get => (_f & 0x40) != 0; set => SetFlag(0x40, value); }
    public bool FlagH { get => (_f & 0x20) != 0; set => SetFlag(0x20, value); }
    public bool FlagC { get => (_f & 0x10) != 0; set => SetFlag(0x10, value); }

    private void SetFlag(byte mask, bool on)
    {
        if (on) _f |= mask;
        else _f &= (byte)~mask;
    }
}
