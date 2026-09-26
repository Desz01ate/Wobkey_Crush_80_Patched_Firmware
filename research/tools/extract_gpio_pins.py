#!/usr/bin/env python3
"""
Extract GPIO pin assignments from Wobkey Crush 80 firmware binary.

Targets the matrix scan code region and GPIO configuration to determine
which pins are used for rows (output) and columns (input).

Telink TLSR9518 (B91) GPIO memory map:
  Base: 0x80140000
  0x80140000-0x8014000B: GPIO_PA-PE input enable (ie)
  0x80140100-0x8014010B: GPIO_PA-PE output (data_out)
  0x80140120-0x8014012B: GPIO_PA-PE output enable (oe)
  0x80140140-0x8014014B: GPIO_PA-PE data_in (read pin state)
  0x80140160-0x8014016B: GPIO_PA-PE pull-up/down config
  0x80140180-0x8014018B: GPIO_PA-PE drive strength
  0x80140300-0x8014030B: GPIO_PA-PE function enable (gpio_en)

Port offsets from base:
  PA=0x00, PB=0x01, PC=0x02, PD=0x03, PE=0x04

Telink GPIO pin encoding (gpio_pin_e):
  GPIO_PA0=0x000001, PA1=0x000002, ..., PA7=0x000080
  GPIO_PB0=0x000100, PB1=0x000200, ..., PB7=0x008000
  GPIO_PC0=0x010000, PC1=0x020000, ..., PC7=0x800000
  GPIO_PD0=0x01000000, ...
  GPIO_PE0=0x0100000000, ...

  Basically: port_index * 8 bits shift, then bit position within port.
"""

import sys
import struct
from pathlib import Path

try:
    from capstone import *
except ImportError:
    print("pip install capstone")
    sys.exit(1)

FW_PATH = Path(__file__).resolve().parents[2] / "firmware/sources/firmware.bin"
# RISC-V code loads at 0x20000000 on TLSR9518
LOAD_ADDR = 0x20000000

# GPIO register bases
GPIO_BASE = 0x80140000
GPIO_OUT   = 0x80140100  # output data
GPIO_OE    = 0x80140120  # output enable
GPIO_IN    = 0x80140140  # input (read)  -- actually some docs say 0x80140200
GPIO_IE    = 0x80140000  # input enable
GPIO_PU    = 0x80140160  # pull-up/down
GPIO_FEN   = 0x80140300  # function enable

# Alternative: some Telink datasheets use 0x80140200 for input
GPIO_IN_ALT = 0x80140200

PORT_NAMES = {0: 'PA', 1: 'PB', 2: 'PC', 3: 'PD', 4: 'PE'}

def port_offset_to_name(offset):
    """Convert a byte offset from GPIO base to port name."""
    port = offset & 0x0F  # low nibble is port index
    return PORT_NAMES.get(port, f'P?({port})')

def analyze_region(fw_data, start_offset, end_offset, label=""):
    """Disassemble a region and find GPIO-related instructions."""
    md = Cs(CS_ARCH_RISCV, CS_MODE_RISCV32)
    md.detail = True

    code = fw_data[start_offset:end_offset]
    base_addr = LOAD_ADDR + start_offset

    print(f"\n{'='*70}")
    print(f"Region: {label} (0x{start_offset:05X} - 0x{end_offset:05X})")
    print(f"{'='*70}")

    # Track register values for data flow analysis
    regs = {}
    gpio_accesses = []

    for insn in md.disasm(code, base_addr):
        line = f"  0x{insn.address:08x}: {insn.mnemonic:8s} {insn.op_str}"

        # Track LUI instructions (load upper immediate)
        if insn.mnemonic == 'lui':
            parts = insn.op_str.split(',')
            if len(parts) == 2:
                reg = parts[0].strip()
                try:
                    imm = int(parts[1].strip(), 0)
                    regs[reg] = imm << 12  # LUI shifts left by 12
                    if 0x80140000 <= regs[reg] <= 0x80150000:
                        print(f"{line}  << GPIO base 0x{regs[reg]:08X}")
                        continue
                except:
                    pass

        # Track ADDI (adds immediate to register)
        if insn.mnemonic == 'addi':
            parts = insn.op_str.split(',')
            if len(parts) == 3:
                dst = parts[0].strip()
                src = parts[1].strip()
                try:
                    imm = int(parts[2].strip(), 0)
                    if src in regs:
                        regs[dst] = regs[src] + imm
                        if 0x80140000 <= regs[dst] <= 0x80150000:
                            print(f"{line}  << GPIO addr 0x{regs[dst]:08X}")
                            continue
                except:
                    pass

        # Track LI (pseudo-instruction, sometimes shows as lui+addi)
        if insn.mnemonic == 'li':
            parts = insn.op_str.split(',')
            if len(parts) == 2:
                reg = parts[0].strip()
                try:
                    imm = int(parts[1].strip(), 0)
                    regs[reg] = imm
                except:
                    pass

        # Store byte/word to GPIO register
        if insn.mnemonic in ('sb', 'sh', 'sw'):
            parts = insn.op_str.replace('(', ',').replace(')', '').split(',')
            if len(parts) >= 2:
                try:
                    # Format: sb rs2, offset(rs1)
                    src_reg = parts[0].strip()
                    offset_str = parts[1].strip()
                    base_reg = parts[2].strip() if len(parts) > 2 else None
                    offset = int(offset_str, 0) if offset_str else 0

                    if base_reg and base_reg in regs:
                        addr = regs[base_reg] + offset
                        if 0x80140000 <= addr <= 0x80150000:
                            reg_type = ""
                            if 0x80140100 <= addr < 0x80140110:
                                reg_type = f"GPIO_OUT {port_offset_to_name(addr - 0x80140100)}"
                            elif 0x80140120 <= addr < 0x80140130:
                                reg_type = f"GPIO_OE {port_offset_to_name(addr - 0x80140120)}"
                            elif 0x80140160 <= addr < 0x80140170:
                                reg_type = f"GPIO_PULLUP {port_offset_to_name(addr - 0x80140160)}"
                            elif 0x80140300 <= addr < 0x80140310:
                                reg_type = f"GPIO_FUNC_EN {port_offset_to_name(addr - 0x80140300)}"
                            elif 0x80140000 <= addr < 0x80140010:
                                reg_type = f"GPIO_IE {port_offset_to_name(addr - 0x80140000)}"
                            elif 0x80140200 <= addr < 0x80140210:
                                reg_type = f"GPIO_IN_ALT {port_offset_to_name(addr - 0x80140200)}"
                            else:
                                reg_type = f"GPIO reg 0x{addr:08X}"

                            gpio_accesses.append(('W', addr, reg_type, insn.address))
                            print(f"{line}  << WRITE {reg_type} @ 0x{addr:08X}")
                            continue
                except:
                    pass

        # Load byte/word from GPIO register
        if insn.mnemonic in ('lb', 'lbu', 'lh', 'lhu', 'lw'):
            parts = insn.op_str.replace('(', ',').replace(')', '').split(',')
            if len(parts) >= 2:
                try:
                    dst_reg = parts[0].strip()
                    offset_str = parts[1].strip()
                    base_reg = parts[2].strip() if len(parts) > 2 else None
                    offset = int(offset_str, 0) if offset_str else 0

                    if base_reg and base_reg in regs:
                        addr = regs[base_reg] + offset
                        if 0x80140000 <= addr <= 0x80150000:
                            reg_type = ""
                            if 0x80140100 <= addr < 0x80140110:
                                reg_type = f"GPIO_OUT {port_offset_to_name(addr - 0x80140100)}"
                            elif 0x80140120 <= addr < 0x80140130:
                                reg_type = f"GPIO_OE {port_offset_to_name(addr - 0x80140120)}"
                            elif 0x80140140 <= addr < 0x80140150:
                                reg_type = f"GPIO_IN {port_offset_to_name(addr - 0x80140140)}"
                            elif 0x80140200 <= addr < 0x80140210:
                                reg_type = f"GPIO_IN_ALT {port_offset_to_name(addr - 0x80140200)}"
                            elif 0x80140300 <= addr < 0x80140310:
                                reg_type = f"GPIO_FUNC_EN {port_offset_to_name(addr - 0x80140300)}"
                            elif 0x80140000 <= addr < 0x80140010:
                                reg_type = f"GPIO_IE {port_offset_to_name(addr - 0x80140000)}"
                            else:
                                reg_type = f"GPIO reg 0x{addr:08X}"

                            gpio_accesses.append(('R', addr, reg_type, insn.address))
                            print(f"{line}  << READ {reg_type} @ 0x{addr:08X}")
                            continue
                except:
                    pass

        # Print any instruction that references 0x80140 in its operands
        if '0x80140' in insn.op_str or '80140' in insn.op_str:
            print(f"{line}  << GPIO reference")

    if gpio_accesses:
        print(f"\n  --- GPIO Access Summary ---")
        for rw, addr, desc, pc in gpio_accesses:
            print(f"  [{rw}] 0x{addr:08X} {desc} (at PC 0x{pc:08X})")

    return gpio_accesses


def scan_for_gpio_refs(fw_data):
    """Scan entire firmware for regions that reference GPIO registers."""
    print("\nScanning firmware for GPIO register references (0x80140)...")

    # Look for LUI instructions loading 0x80140 (upper 20 bits)
    # LUI encoding: imm[31:12] | rd | 0110111
    # 0x80140 << 12 = 0x80140000
    # We need imm[31:12] = 0x80140

    hits = []
    for i in range(0, len(fw_data) - 4, 2):  # RISC-V can have 2-byte compressed
        word = struct.unpack_from('<I', fw_data, i)[0]
        # Check for LUI with imm = 0x80140
        if (word & 0x7F) == 0x37:  # LUI opcode
            imm = (word >> 12) & 0xFFFFF
            if imm == 0x80140:
                hits.append(i)

    print(f"Found {len(hits)} LUI 0x80140 instructions:")
    for h in hits:
        print(f"  offset 0x{h:05X} (addr 0x{LOAD_ADDR + h:08X})")

    return hits


def find_data_tables(fw_data):
    """Look for GPIO pin value tables in data sections.

    Telink GPIO pin values are 32-bit:
      PA0=0x00000001, PA7=0x00000080
      PB0=0x00000100, PB7=0x00008000
      PC0=0x00010000, PC7=0x00800000
      PD0=0x01000000, PD7=0x08000000 (wait, actually bit-per-pin within port byte)

    Actually in Telink SDK, gpio_pin_e is:
      GPIO_PA0 = GPIO_GROUPA | BIT(0) = 0x000 | 0x01 = 0x001
      ...but that's 16-bit or 32-bit depending on SDK version.

    In B91 SDK specifically:
      typedef enum {
        GPIO_PA0 = 0x00 | BIT(0), // = 0x01
        GPIO_PA1 = 0x00 | BIT(1), // = 0x02
        ...
        GPIO_PB0 = 0x100 | BIT(0), // = 0x101  -- wait no

    Let me check: GPIO_GROUPA=0x00, GPIO_GROUPB=0x100, GPIO_GROUPC=0x200, etc.
    So:
      GPIO_PB4 = 0x100 | 0x10 = 0x110
      GPIO_PC3 = 0x200 | 0x08 = 0x208

    These are stored as 32-bit words. Let's search for arrays of such values.
    """

    print("\n\nSearching for GPIO pin tables in firmware data...")

    # Valid GPIO pin values (B91): group | bit
    # Groups: PA=0x000, PB=0x100, PC=0x200, PD=0x300, PE=0x400
    valid_pins = set()
    for group_idx, group_base in enumerate([0x000, 0x100, 0x200, 0x300, 0x400]):
        for bit in range(8):
            pin_val = group_base | (1 << bit)
            valid_pins.add(pin_val)

    def pin_name(val):
        group = (val >> 8) & 0xFF
        bits = val & 0xFF
        port = PORT_NAMES.get(group, f'P{group}')
        for b in range(8):
            if bits == (1 << b):
                return f"GPIO_{port}{b}"
        return f"GPIO_{port}(0x{bits:02X})"

    # Search for sequences of 8 consecutive valid GPIO pin values (row pins)
    # stored as 32-bit little-endian words
    print("\n  Looking for 8-entry pin arrays (rows)...")
    for i in range(0, len(fw_data) - 32, 4):
        vals = [struct.unpack_from('<I', fw_data, i + j*4)[0] for j in range(8)]
        # Check if all 8 are valid GPIO pins
        if all(v in valid_pins for v in vals):
            names = [pin_name(v) for v in vals]
            # Filter: skip if all same pin
            if len(set(vals)) > 1:
                print(f"  offset 0x{i:05X}: {names}")

    # Search for sequences of 16 consecutive valid GPIO pin values (col pins)
    print("\n  Looking for 16-entry pin arrays (columns)...")
    for i in range(0, len(fw_data) - 64, 4):
        vals = [struct.unpack_from('<I', fw_data, i + j*4)[0] for j in range(16)]
        if all(v in valid_pins for v in vals):
            names = [pin_name(v) for v in vals]
            if len(set(vals)) > 1:
                print(f"  offset 0x{i:05X}: {names}")

    # Also try 16-bit entries (some SDK versions use uint16)
    print("\n  Looking for 8-entry pin arrays as uint16 (rows)...")
    for i in range(0, len(fw_data) - 16, 2):
        vals = [struct.unpack_from('<H', fw_data, i + j*2)[0] for j in range(8)]
        if all(v in valid_pins for v in vals):
            names = [pin_name(v) for v in vals]
            if len(set(vals)) > 1:
                print(f"  offset 0x{i:05X}: {names}")

    print("\n  Looking for 16-entry pin arrays as uint16 (columns)...")
    for i in range(0, len(fw_data) - 32, 2):
        vals = [struct.unpack_from('<H', fw_data, i + j*2)[0] for j in range(16)]
        if all(v in valid_pins for v in vals):
            names = [pin_name(v) for v in vals]
            if len(set(vals)) > 1:
                print(f"  offset 0x{i:05X}: {names}")


def main():
    with open(FW_PATH, 'rb') as f:
        fw_data = f.read()

    print(f"Firmware size: {len(fw_data)} bytes")

    # 1. Scan for all GPIO base references
    gpio_hits = scan_for_gpio_refs(fw_data)

    # 2. Search for GPIO pin tables in data sections
    find_data_tables(fw_data)

    # 3. Disassemble key regions around GPIO references
    # Group nearby hits and analyze surrounding code
    regions_analyzed = set()
    for hit in gpio_hits:
        # Analyze a window around each GPIO reference
        region_start = max(0, hit - 64)
        region_end = min(len(fw_data), hit + 128)

        # Round to avoid overlapping analyses
        region_key = region_start // 256
        if region_key not in regions_analyzed:
            regions_analyzed.add(region_key)
            analyze_region(fw_data, region_start, region_end,
                         f"GPIO ref at 0x{hit:05X}")

    # 4. Specifically analyze the known matrix scan region
    print("\n" + "="*70)
    print("DETAILED: Matrix scan region 0x01300-0x01700")
    print("="*70)
    analyze_region(fw_data, 0x01300, 0x01700, "Matrix scan (known)")

    # 5. Look wider - the init function that sets up GPIO for matrix
    # is likely called before the scan function
    print("\n" + "="*70)
    print("DETAILED: Pre-matrix region 0x01000-0x01300")
    print("="*70)
    analyze_region(fw_data, 0x01000, 0x01300, "Pre-matrix init")


if __name__ == '__main__':
    main()
