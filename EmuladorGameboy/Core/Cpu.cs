namespace EmuladorGameboy.Core;

/// <summary>
/// CPU Sharp LR35902 (o core costuma ser chamado de "SM83").
///
/// Faz só uma coisa, repetidamente: o ciclo FETCH (lê o opcode em PC) →
/// DECODE (descobre que instrução é) → EXECUTE (faz a operação, mexe flags,
/// gasta ciclos). Ela não conhece PPU, som nem botões — toda comunicação com o
/// resto do console passa pela MMU (ReadByte/WriteByte).
///
/// Truque de implementação: o conjunto de instruções tem MUITA regularidade.
/// Em vez de 256+256 casos, decodificamos por campos de bits do opcode:
///   - bits 0-2  -> qual registrador (B,C,D,E,H,L,(HL),A)
///   - bits 3-5  -> qual operação / destino
/// Isso cobre centenas de opcodes em poucas linhas e MOSTRA a estrutura da ISA.
/// </summary>
internal sealed class Cpu
{
    private readonly Registers _reg = new();
    private readonly Mmu _mmu;

    private bool _ime;    // Interrupt Master Enable: a "chave geral" das interrupções
    private bool _halted; // CPU dormindo até a próxima interrupção (instrução HALT)

    public Cpu(Mmu mmu)
    {
        _mmu = mmu;
        Reset();
    }

    public Registers Registers => _reg;

    /// <summary>
    /// Estado dos registradores logo APÓS a Boot ROM no DMG. Como pulamos a Boot
    /// ROM (Fase 1), setamos esses valores "na mão" para começar exatamente como
    /// um Game Boy real começaria o cartucho.
    /// </summary>
    public void Reset()
    {
        _reg.AF = 0x01B0;
        _reg.BC = 0x0013;
        _reg.DE = 0x00D8;
        _reg.HL = 0x014D;
        _reg.SP = 0xFFFE;
        _reg.PC = 0x0100; // entry point do cartucho
        _ime = false;
        _halted = false;
    }

    /// <summary>
    /// Executa UMA instrução (ou despacha uma interrupção pendente).
    /// Retorna quantos T-cycles a operação custou — é com esse número que, mais
    /// tarde, vamos avançar PPU, timers e som em sincronia.
    /// </summary>
    public int Step()
    {
        int interruptCycles = HandleInterrupts();
        if (interruptCycles > 0) return interruptCycles;

        if (_halted) return 4; // dormindo: consome tempo sem executar nada

        byte opcode = FetchByte();
        return Execute(opcode);
    }

    // ===================== Fetch (lê e avança PC) =====================
    private byte FetchByte() => _mmu.ReadByte(_reg.PC++);

    private ushort FetchWord()
    {
        ushort w = _mmu.ReadWord(_reg.PC);
        _reg.PC += 2;
        return w;
    }

    // ===================== Interrupções =====================
    // 5 fontes, em ordem de prioridade (bit 0 = maior):
    //   bit 0 VBlank (0x40), 1 LCD STAT (0x48), 2 Timer (0x50),
    //   bit 3 Serial (0x58), 4 Joypad (0x60).
    // IF = 0xFF0F (pendentes), IE = 0xFFFF (permitidas), IME = chave geral.
    private int HandleInterrupts()
    {
        byte ifReg = _mmu.ReadByte(0xFF0F);
        byte ieReg = _mmu.ReadByte(0xFFFF);
        int pending = ifReg & ieReg & 0x1F;
        if (pending == 0) return 0;

        // Uma interrupção pendente acorda a CPU do HALT mesmo com IME desligado.
        _halted = false;
        if (!_ime) return 0;

        _ime = false; // ao atender, desliga a chave geral (o jogo religa com EI/RETI)
        for (int i = 0; i < 5; i++)
        {
            if ((pending & (1 << i)) != 0)
            {
                _mmu.WriteByte(0xFF0F, (byte)(ifReg & ~(1 << i))); // limpa o bit atendido
                Push(_reg.PC);                                      // empilha o retorno
                _reg.PC = (ushort)(0x40 + i * 8);                   // salta pro vetor
                return 20;
            }
        }
        return 0;
    }

    // ===================== Acesso a registrador por índice =====================
    // Índice: 0=B 1=C 2=D 3=E 4=H 5=L 6=(HL) 7=A  — exatamente a ordem dos bits do opcode.
    private byte ReadReg(int index) => index switch
    {
        0 => _reg.B,
        1 => _reg.C,
        2 => _reg.D,
        3 => _reg.E,
        4 => _reg.H,
        5 => _reg.L,
        6 => _mmu.ReadByte(_reg.HL), // (HL) = o byte apontado por HL
        _ => _reg.A,
    };

    private void WriteReg(int index, byte value)
    {
        switch (index)
        {
            case 0: _reg.B = value; break;
            case 1: _reg.C = value; break;
            case 2: _reg.D = value; break;
            case 3: _reg.E = value; break;
            case 4: _reg.H = value; break;
            case 5: _reg.L = value; break;
            case 6: _mmu.WriteByte(_reg.HL, value); break;
            default: _reg.A = value; break;
        }
    }

    // ===================== Pilha =====================
    // A pilha cresce pra BAIXO: PUSH decrementa SP e grava; POP lê e incrementa.
    private void Push(ushort value)
    {
        _reg.SP -= 2;
        _mmu.WriteWord(_reg.SP, value);
    }

    private ushort Pop()
    {
        ushort v = _mmu.ReadWord(_reg.SP);
        _reg.SP += 2;
        return v;
    }

    // ===================== ALU (operações aritméticas/lógicas) =====================
    private void AluAdd(byte v, bool withCarry)
    {
        int carry = withCarry && _reg.FlagC ? 1 : 0;
        int result = _reg.A + v + carry;
        _reg.FlagH = ((_reg.A & 0xF) + (v & 0xF) + carry) > 0xF; // vazou do bit 3?
        _reg.FlagC = result > 0xFF;                               // vazou do bit 7?
        _reg.A = (byte)result;
        _reg.FlagZ = _reg.A == 0;
        _reg.FlagN = false;
    }

    private void AluSub(byte v, bool withCarry, bool store = true)
    {
        int carry = withCarry && _reg.FlagC ? 1 : 0;
        int result = _reg.A - v - carry;
        _reg.FlagH = ((_reg.A & 0xF) - (v & 0xF) - carry) < 0; // pediu emprestado no nibble?
        _reg.FlagC = result < 0;                                // pediu emprestado no total?
        byte b = (byte)result;
        _reg.FlagZ = b == 0;
        _reg.FlagN = true;
        if (store) _reg.A = b; // CP é igual a SUB mas SEM guardar o resultado
    }

    private void AluAnd(byte v)
    {
        _reg.A &= v;
        _reg.FlagZ = _reg.A == 0; _reg.FlagN = false; _reg.FlagH = true; _reg.FlagC = false;
    }
    private void AluOr(byte v)
    {
        _reg.A |= v;
        _reg.FlagZ = _reg.A == 0; _reg.FlagN = false; _reg.FlagH = false; _reg.FlagC = false;
    }
    private void AluXor(byte v)
    {
        _reg.A ^= v;
        _reg.FlagZ = _reg.A == 0; _reg.FlagN = false; _reg.FlagH = false; _reg.FlagC = false;
    }
    private void AluCp(byte v) => AluSub(v, false, store: false);

    private byte Inc8(byte v)
    {
        byte r = (byte)(v + 1);
        _reg.FlagZ = r == 0; _reg.FlagN = false; _reg.FlagH = (v & 0xF) == 0xF;
        return r; // INC/DEC NÃO mexem na flag C
    }
    private byte Dec8(byte v)
    {
        byte r = (byte)(v - 1);
        _reg.FlagZ = r == 0; _reg.FlagN = true; _reg.FlagH = (v & 0xF) == 0;
        return r;
    }

    private void AddHL(ushort v)
    {
        int result = _reg.HL + v;
        _reg.FlagN = false;
        _reg.FlagH = ((_reg.HL & 0x0FFF) + (v & 0x0FFF)) > 0x0FFF; // half-carry de 16 bits = bit 11
        _reg.FlagC = result > 0xFFFF;
        _reg.HL = (ushort)result; // ADD HL,rr NÃO mexe na flag Z
    }

    // Rotações do acumulador (versões "A": Z é SEMPRE 0, diferente das versões CB)
    private void RlcA() { int c = (_reg.A >> 7) & 1; _reg.A = (byte)((_reg.A << 1) | c); SetA_RotFlags(c); }
    private void RrcA() { int c = _reg.A & 1; _reg.A = (byte)((_reg.A >> 1) | (c << 7)); SetA_RotFlags(c); }
    private void RlA()  { int c = (_reg.A >> 7) & 1; _reg.A = (byte)((_reg.A << 1) | (_reg.FlagC ? 1 : 0)); SetA_RotFlags(c); }
    private void RrA()  { int c = _reg.A & 1; _reg.A = (byte)((_reg.A >> 1) | (_reg.FlagC ? 0x80 : 0)); SetA_RotFlags(c); }
    private void SetA_RotFlags(int carry)
    {
        _reg.FlagZ = false; _reg.FlagN = false; _reg.FlagH = false; _reg.FlagC = carry != 0;
    }

    private void Daa()
    {
        // Ajuste decimal após soma/subtração em BCD. Usa as flags N/H/C deixadas
        // pela operação anterior para "consertar" A de volta a um par de dígitos.
        int a = _reg.A;
        if (!_reg.FlagN)
        {
            if (_reg.FlagC || a > 0x99) { a += 0x60; _reg.FlagC = true; }
            if (_reg.FlagH || (a & 0x0F) > 0x09) { a += 0x06; }
        }
        else
        {
            if (_reg.FlagC) a -= 0x60;
            if (_reg.FlagH) a -= 0x06;
        }
        a &= 0xFF;
        _reg.FlagZ = a == 0;
        _reg.FlagH = false;
        _reg.A = (byte)a;
    }

    // ADD SP,e8 e LD HL,SP+e8: flags H/C calculadas como soma de 8 bits (parte baixa). Z e N = 0.
    private void AddSpE8()
    {
        sbyte e = (sbyte)FetchByte();
        int sp = _reg.SP;
        _reg.FlagZ = false; _reg.FlagN = false;
        _reg.FlagH = ((sp & 0xF) + (e & 0xF)) > 0xF;
        _reg.FlagC = ((sp & 0xFF) + (e & 0xFF)) > 0xFF;
        _reg.SP = (ushort)(sp + e);
    }
    private void LdHlSpE8()
    {
        sbyte e = (sbyte)FetchByte();
        int sp = _reg.SP;
        _reg.FlagZ = false; _reg.FlagN = false;
        _reg.FlagH = ((sp & 0xF) + (e & 0xF)) > 0xF;
        _reg.FlagC = ((sp & 0xFF) + (e & 0xFF)) > 0xFF;
        _reg.HL = (ushort)(sp + e);
    }

    // ===================== Controle de fluxo (com timing condicional) =====================
    private int Jr(bool cond)
    {
        sbyte offset = (sbyte)FetchByte(); // o operando é SEMPRE lido (mesmo se não saltar)
        if (cond) { _reg.PC = (ushort)(_reg.PC + offset); return 12; }
        return 8;
    }
    private int JpCond(bool cond)
    {
        ushort target = FetchWord();
        if (cond) { _reg.PC = target; return 16; }
        return 12;
    }
    private int CallCond(bool cond)
    {
        ushort target = FetchWord();
        if (cond) { Push(_reg.PC); _reg.PC = target; return 24; }
        return 12;
    }
    private int RetCond(bool cond)
    {
        if (cond) { _reg.PC = Pop(); return 20; }
        return 8;
    }

    // ===================== DECODE / EXECUTE da tabela principal =====================
    private int Execute(byte op)
    {
        switch (op)
        {
            // ---- Bloco regular LD r, r'  (0x40-0x7F), com 0x76 = HALT no meio ----
            case 0x76: _halted = true; return 4;
            case >= 0x40 and <= 0x7F:
            {
                int dst = (op >> 3) & 7;
                int src = op & 7;
                WriteReg(dst, ReadReg(src));
                return (dst == 6 || src == 6) ? 8 : 4; // tocar em (HL) custa 1 acesso a mais
            }

            // ---- Bloco regular ALU A, r  (0x80-0xBF) ----
            case >= 0x80 and <= 0xBF:
            {
                int kind = (op >> 3) & 7; // ADD ADC SUB SBC AND XOR OR CP
                byte v = ReadReg(op & 7);
                switch (kind)
                {
                    case 0: AluAdd(v, false); break;
                    case 1: AluAdd(v, true); break;
                    case 2: AluSub(v, false); break;
                    case 3: AluSub(v, true); break;
                    case 4: AluAnd(v); break;
                    case 5: AluXor(v); break;
                    case 6: AluOr(v); break;
                    default: AluCp(v); break;
                }
                return (op & 7) == 6 ? 8 : 4;
            }

            // ---- Diversos (tabela 0x00-0x3F e 0xC0-0xFF) ----
            case 0x00: return 4;                 // NOP
            case 0x10: FetchByte(); return 4;    // STOP (simplificado)
            case 0xCB: return ExecuteCb(FetchByte());

            // LD rr, d16
            case 0x01: _reg.BC = FetchWord(); return 12;
            case 0x11: _reg.DE = FetchWord(); return 12;
            case 0x21: _reg.HL = FetchWord(); return 12;
            case 0x31: _reg.SP = FetchWord(); return 12;

            // LD (rr), A  /  LD A, (rr)   — incluindo os autoincremento/decremento de HL
            case 0x02: _mmu.WriteByte(_reg.BC, _reg.A); return 8;
            case 0x12: _mmu.WriteByte(_reg.DE, _reg.A); return 8;
            case 0x22: _mmu.WriteByte(_reg.HL, _reg.A); _reg.HL++; return 8; // LD (HL+),A
            case 0x32: _mmu.WriteByte(_reg.HL, _reg.A); _reg.HL--; return 8; // LD (HL-),A
            case 0x0A: _reg.A = _mmu.ReadByte(_reg.BC); return 8;
            case 0x1A: _reg.A = _mmu.ReadByte(_reg.DE); return 8;
            case 0x2A: _reg.A = _mmu.ReadByte(_reg.HL); _reg.HL++; return 8; // LD A,(HL+)
            case 0x3A: _reg.A = _mmu.ReadByte(_reg.HL); _reg.HL--; return 8; // LD A,(HL-)

            // INC/DEC rr (16 bits) — NÃO afetam flags
            case 0x03: _reg.BC++; return 8;
            case 0x13: _reg.DE++; return 8;
            case 0x23: _reg.HL++; return 8;
            case 0x33: _reg.SP++; return 8;
            case 0x0B: _reg.BC--; return 8;
            case 0x1B: _reg.DE--; return 8;
            case 0x2B: _reg.HL--; return 8;
            case 0x3B: _reg.SP--; return 8;

            // INC/DEC r (8 bits) — regulares por (op>>3)&7
            case 0x04 or 0x0C or 0x14 or 0x1C or 0x24 or 0x2C or 0x34 or 0x3C:
            {
                int idx = (op >> 3) & 7;
                WriteReg(idx, Inc8(ReadReg(idx)));
                return idx == 6 ? 12 : 4;
            }
            case 0x05 or 0x0D or 0x15 or 0x1D or 0x25 or 0x2D or 0x35 or 0x3D:
            {
                int idx = (op >> 3) & 7;
                WriteReg(idx, Dec8(ReadReg(idx)));
                return idx == 6 ? 12 : 4;
            }

            // LD r, d8 — regulares por (op>>3)&7
            case 0x06 or 0x0E or 0x16 or 0x1E or 0x26 or 0x2E or 0x36 or 0x3E:
            {
                int idx = (op >> 3) & 7;
                WriteReg(idx, FetchByte());
                return idx == 6 ? 12 : 8;
            }

            // ADD HL, rr
            case 0x09: AddHL(_reg.BC); return 8;
            case 0x19: AddHL(_reg.DE); return 8;
            case 0x29: AddHL(_reg.HL); return 8;
            case 0x39: AddHL(_reg.SP); return 8;

            // Rotações do acumulador
            case 0x07: RlcA(); return 4;
            case 0x0F: RrcA(); return 4;
            case 0x17: RlA(); return 4;
            case 0x1F: RrA(); return 4;

            // JR (salto relativo)
            case 0x18: return Jr(true);
            case 0x20: return Jr(!_reg.FlagZ);
            case 0x28: return Jr(_reg.FlagZ);
            case 0x30: return Jr(!_reg.FlagC);
            case 0x38: return Jr(_reg.FlagC);

            // JP (salto absoluto)
            case 0xC3: _reg.PC = FetchWord(); return 16;
            case 0xC2: return JpCond(!_reg.FlagZ);
            case 0xCA: return JpCond(_reg.FlagZ);
            case 0xD2: return JpCond(!_reg.FlagC);
            case 0xDA: return JpCond(_reg.FlagC);
            case 0xE9: _reg.PC = _reg.HL; return 4; // JP (HL)

            // CALL / RET / RETI
            case 0xCD: { ushort a = FetchWord(); Push(_reg.PC); _reg.PC = a; return 24; }
            case 0xC4: return CallCond(!_reg.FlagZ);
            case 0xCC: return CallCond(_reg.FlagZ);
            case 0xD4: return CallCond(!_reg.FlagC);
            case 0xDC: return CallCond(_reg.FlagC);
            case 0xC9: _reg.PC = Pop(); return 16;
            case 0xC0: return RetCond(!_reg.FlagZ);
            case 0xC8: return RetCond(_reg.FlagZ);
            case 0xD0: return RetCond(!_reg.FlagC);
            case 0xD8: return RetCond(_reg.FlagC);
            case 0xD9: _reg.PC = Pop(); _ime = true; return 16; // RETI = RET + religa IME

            // RST — chamadas rápidas para vetores fixos (0x00,0x08,...,0x38)
            case 0xC7 or 0xCF or 0xD7 or 0xDF or 0xE7 or 0xEF or 0xF7 or 0xFF:
            { Push(_reg.PC); _reg.PC = (ushort)(op & 0x38); return 16; }

            // PUSH / POP
            case 0xC5: Push(_reg.BC); return 16;
            case 0xD5: Push(_reg.DE); return 16;
            case 0xE5: Push(_reg.HL); return 16;
            case 0xF5: Push(_reg.AF); return 16;
            case 0xC1: _reg.BC = Pop(); return 12;
            case 0xD1: _reg.DE = Pop(); return 12;
            case 0xE1: _reg.HL = Pop(); return 12;
            case 0xF1: _reg.AF = Pop(); return 12;

            // ALU A, d8 (operando imediato)
            case 0xC6: AluAdd(FetchByte(), false); return 8;
            case 0xCE: AluAdd(FetchByte(), true); return 8;
            case 0xD6: AluSub(FetchByte(), false); return 8;
            case 0xDE: AluSub(FetchByte(), true); return 8;
            case 0xE6: AluAnd(FetchByte()); return 8;
            case 0xEE: AluXor(FetchByte()); return 8;
            case 0xF6: AluOr(FetchByte()); return 8;
            case 0xFE: AluCp(FetchByte()); return 8;

            // Acessos à página de I/O (0xFF00 + n) e endereço absoluto
            case 0xE0: _mmu.WriteByte((ushort)(0xFF00 + FetchByte()), _reg.A); return 12; // LDH (a8),A
            case 0xF0: _reg.A = _mmu.ReadByte((ushort)(0xFF00 + FetchByte())); return 12; // LDH A,(a8)
            case 0xE2: _mmu.WriteByte((ushort)(0xFF00 + _reg.C), _reg.A); return 8;        // LD (C),A
            case 0xF2: _reg.A = _mmu.ReadByte((ushort)(0xFF00 + _reg.C)); return 8;        // LD A,(C)
            case 0xEA: _mmu.WriteByte(FetchWord(), _reg.A); return 16;                     // LD (a16),A
            case 0xFA: _reg.A = _mmu.ReadByte(FetchWord()); return 16;                     // LD A,(a16)

            // Interrupções e bandeiras diversas
            case 0xF3: _ime = false; return 4;                                            // DI
            case 0xFB: _ime = true; return 4;                                             // EI (sem o delay de 1 instr.)
            case 0x2F: _reg.A = (byte)~_reg.A; _reg.FlagN = true; _reg.FlagH = true; return 4; // CPL
            case 0x37: _reg.FlagC = true; _reg.FlagN = false; _reg.FlagH = false; return 4;    // SCF
            case 0x3F: _reg.FlagC = !_reg.FlagC; _reg.FlagN = false; _reg.FlagH = false; return 4; // CCF
            case 0x27: Daa(); return 4;                                                    // DAA

            // SP / endereço extras
            case 0x08: { ushort a = FetchWord(); _mmu.WriteWord(a, _reg.SP); return 20; }  // LD (a16),SP
            case 0xF9: _reg.SP = _reg.HL; return 8;                                        // LD SP,HL
            case 0xE8: AddSpE8(); return 16;                                               // ADD SP,e8
            case 0xF8: LdHlSpE8(); return 12;                                              // LD HL,SP+e8

            default:
                throw new NotImplementedException(
                    $"Opcode ilegal/não implementado: 0x{op:X2} em PC=0x{(ushort)(_reg.PC - 1):X4}");
        }
    }

    // ===================== Tabela CB (prefixo 0xCB) =====================
    // Extremamente regular:
    //   bits 6-7 = bloco: 0=rot/shift, 1=BIT, 2=RES, 3=SET
    //   bits 3-5 = operação OU número do bit
    //   bits 0-2 = qual registrador
    private int ExecuteCb(byte op)
    {
        int block = op >> 6;
        int sel = (op >> 3) & 7;
        int idx = op & 7;
        byte value = ReadReg(idx);
        int cycles = idx == 6 ? 16 : 8;

        switch (block)
        {
            case 0: // rotações e deslocamentos
                byte result = sel switch
                {
                    0 => Rlc(value),
                    1 => Rrc(value),
                    2 => Rl(value),
                    3 => Rr(value),
                    4 => Sla(value),
                    5 => Sra(value),
                    6 => Swap(value),
                    _ => Srl(value),
                };
                WriteReg(idx, result);
                return cycles;

            case 1: // BIT b, r — testa um bit (só mexe flags; não grava)
                _reg.FlagZ = (value & (1 << sel)) == 0;
                _reg.FlagN = false;
                _reg.FlagH = true;
                return idx == 6 ? 12 : 8;

            case 2: // RES b, r — zera um bit
                WriteReg(idx, (byte)(value & ~(1 << sel)));
                return cycles;

            default: // SET b, r — liga um bit
                WriteReg(idx, (byte)(value | (1 << sel)));
                return cycles;
        }
    }

    // Rotações/deslocamentos CB: setam Z conforme o resultado, N=H=0, C=bit que saiu.
    private byte Rlc(byte v)  { int c = (v >> 7) & 1; byte r = (byte)((v << 1) | c); SetRotFlags(r, c); return r; }
    private byte Rrc(byte v)  { int c = v & 1; byte r = (byte)((v >> 1) | (c << 7)); SetRotFlags(r, c); return r; }
    private byte Rl(byte v)   { int c = (v >> 7) & 1; byte r = (byte)((v << 1) | (_reg.FlagC ? 1 : 0)); SetRotFlags(r, c); return r; }
    private byte Rr(byte v)   { int c = v & 1; byte r = (byte)((v >> 1) | (_reg.FlagC ? 0x80 : 0)); SetRotFlags(r, c); return r; }
    private byte Sla(byte v)  { int c = (v >> 7) & 1; byte r = (byte)(v << 1); SetRotFlags(r, c); return r; }
    private byte Sra(byte v)  { int c = v & 1; byte r = (byte)((v >> 1) | (v & 0x80)); SetRotFlags(r, c); return r; } // mantém bit 7 (aritmético)
    private byte Srl(byte v)  { int c = v & 1; byte r = (byte)(v >> 1); SetRotFlags(r, c); return r; }
    private byte Swap(byte v)
    {
        byte r = (byte)((v >> 4) | (v << 4)); // troca os nibbles
        _reg.FlagZ = r == 0; _reg.FlagN = false; _reg.FlagH = false; _reg.FlagC = false;
        return r;
    }
    private void SetRotFlags(byte r, int carry)
    {
        _reg.FlagZ = r == 0; _reg.FlagN = false; _reg.FlagH = false; _reg.FlagC = carry != 0;
    }

    // ===================== Apoio à depuração =====================
    /// <summary>Linha de trace com o estado atual (para acompanhar a execução).</summary>
    public string TraceLine()
    {
        byte op = _mmu.ReadByte(_reg.PC);
        return $"PC:{_reg.PC:X4} op:{op:X2}  AF:{_reg.AF:X4} BC:{_reg.BC:X4} " +
               $"DE:{_reg.DE:X4} HL:{_reg.HL:X4} SP:{_reg.SP:X4} IME:{(_ime ? 1 : 0)}";
    }
}
