namespace ProtoDesigner.CodeGen.C;

/// <summary>
/// The fixed, hand-written C runtime that generated headers include. It is the exact mirror of
/// <see cref="Runtime.BitBuffer"/>: same MSB-first sub-byte packing, same endianness handling on
/// whole-byte fields, same two's-complement sign extension. If one changes, both change.
/// </summary>
/// <remarks>
/// C rather than C++ so the same header serves both languages — a protocol definition should not decide
/// what language the module that speaks it is written in. Everything is <c>static inline</c> in the
/// header: no library to link, no allocation, no exceptions, and nothing that needs a C++ compiler.
/// </remarks>
internal static class CRuntimeHeader
{
    public const string FileName = "protodesigner_runtime.h";

    public static string Emit() => """
// -----------------------------------------------------------------------------
// ProtoDesigner runtime - generated. Do not edit.
//
// A minimal bit-level cursor over a byte buffer. Sub-byte fields pack MSB-first
// within their storage byte; whole-byte fields honour the endianness passed in.
//
// C99, header-only, no dynamic allocation. Compiles as C or as C++.
// -----------------------------------------------------------------------------
#ifndef PROTODESIGNER_RUNTIME_H
#define PROTODESIGNER_RUNTIME_H

#include <stdint.h>
#include <stddef.h>
#include <string.h>

#ifndef __cplusplus
#include <stdbool.h>
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Header-only helpers. 'inline' needs C99 or C++; an older C compiler still gets a working
   translation unit from plain 'static', it just may warn about unused functions. */
#if defined(__cplusplus) || (defined(__STDC_VERSION__) && __STDC_VERSION__ >= 199901L)
#define PD_INLINE static inline
#else
#define PD_INLINE static
#endif

typedef enum pd_endian {
    PD_ENDIAN_LITTLE = 0,
    PD_ENDIAN_BIG = 1
} pd_endian_t;

/* Result of a decode attempt. 'ok' is false when the buffer ran out.
   Named decode_result rather than status so it can never collide with a user message called
   "Status" - protocol authors use that name constantly. */
typedef struct pd_decode_result {
    bool ok;
    size_t bit_offset;   /* where the failure happened, when !ok */
} pd_decode_result_t;

PD_INLINE pd_decode_result_t pd_decode_ok(void) {
    pd_decode_result_t r;
    r.ok = true;
    r.bit_offset = 0;
    return r;
}

PD_INLINE pd_decode_result_t pd_decode_fail(size_t at) {
    pd_decode_result_t r;
    r.ok = false;
    r.bit_offset = at;
    return r;
}

/* Turns a scaled value into a wire code. Rounds to nearest, half away from zero - NOT the truncation
   a plain cast to an integer would do. Truncating biases every reading downward by up to one whole
   code and makes the top of the range unreachable: a -40..70 temperature scaled by 110/255 arrives at
   254.99999999999997 for 70.0, which would truncate to 254 and never reach the 255 the width allows. */
PD_INLINE int64_t pd_quantize(double v) {
    return (int64_t)(v < 0.0 ? v - 0.5 : v + 0.5);
}

/* Reinterprets a float's bits as an integer and back, for fields sent as raw IEEE-754. memcpy is the
   only strictly-conforming route - a union or a pointer cast is undefined behaviour - and compilers
   optimise this away to nothing. */
PD_INLINE uint32_t pd_float_bits(float v) {
    uint32_t bits = 0;
    memcpy(&bits, &v, sizeof(bits));
    return bits;
}

PD_INLINE float pd_bits_to_float(uint32_t bits) {
    float v = 0.0f;
    memcpy(&v, &bits, sizeof(v));
    return v;
}

PD_INLINE uint64_t pd_double_bits(double v) {
    uint64_t bits = 0;
    memcpy(&bits, &v, sizeof(bits));
    return bits;
}

PD_INLINE double pd_bits_to_double(uint64_t bits) {
    double v = 0.0;
    memcpy(&v, &bits, sizeof(v));
    return v;
}

/* ---- writer ------------------------------------------------------------------------------- */

typedef struct pd_bit_writer {
    uint8_t* data;
    size_t capacity_bits;
    size_t cursor;
    bool overflow;
} pd_bit_writer_t;

PD_INLINE void pd_bw_init(pd_bit_writer_t* w, uint8_t* data, size_t capacity_bytes) {
    w->data = data;
    w->capacity_bits = capacity_bytes * 8;
    w->cursor = 0;
    w->overflow = false;
    if (data && capacity_bytes) memset(data, 0, capacity_bytes);
}

PD_INLINE size_t pd_bw_bit_length(const pd_bit_writer_t* w) { return w->cursor; }
PD_INLINE size_t pd_bw_byte_length(const pd_bit_writer_t* w) { return (w->cursor + 7) / 8; }
PD_INLINE bool pd_bw_overflowed(const pd_bit_writer_t* w) { return w->overflow; }
PD_INLINE void pd_bw_skip(pd_bit_writer_t* w, size_t bits) { w->cursor += bits; }

PD_INLINE void pd_bw_pad_to(pd_bit_writer_t* w, size_t alignment_bits) {
    size_t mod;
    if (alignment_bits == 0) return;
    mod = w->cursor % alignment_bits;
    if (mod) w->cursor += (alignment_bits - mod);
}

/* Order in which a value's bits are laid into the stream. MSB-first is the default and what every
   byte-aligned protocol means; LSB-first shows up on UART links that shift the low bit out first. */
typedef enum pd_bit_order { PD_BITS_MSB_FIRST = 0, PD_BITS_LSB_FIRST = 1 } pd_bit_order_t;

PD_INLINE void pd_bw_write_unsigned(pd_bit_writer_t* w, uint64_t value, int width,
                                    pd_endian_t endian, pd_bit_order_t order) {
    int i;
    if (width <= 0 || width > 64) { w->overflow = true; return; }
    if (w->cursor + (size_t)width > w->capacity_bits) { w->overflow = true; return; }

    /* Fast path: byte-aligned cursor and whole-byte width. Only for MSB-first — LSB-first reverses the
       bits inside every byte, so copying bytes across would be wrong rather than merely slower. */
    if (order == PD_BITS_MSB_FIRST && (w->cursor % 8) == 0 && (width % 8) == 0) {
        const int bytes = width / 8;
        uint8_t* p = w->data + (w->cursor / 8);
        if (endian == PD_ENDIAN_BIG) {
            for (i = 0; i < bytes; ++i)
                p[i] = (uint8_t)((value >> ((bytes - 1 - i) * 8)) & 0xFFu);
        } else {
            for (i = 0; i < bytes; ++i)
                p[i] = (uint8_t)((value >> (i * 8)) & 0xFFu);
        }
        w->cursor += (size_t)width;
        return;
    }

    /* Slow path: one bit at a time. The stream always fills from the top of each byte downwards; what
       the bit order changes is which end of the *value* is consumed first. */
    for (i = 0; i < width; ++i) {
        const int source = (order == PD_BITS_LSB_FIRST) ? i : (width - 1 - i);
        const uint64_t bit = (value >> source) & 1u;
        if (bit) {
            const size_t byte_index = w->cursor / 8;
            const int bit_index = 7 - (int)(w->cursor % 8);
            w->data[byte_index] |= (uint8_t)(1u << bit_index);
        }
        ++w->cursor;
    }
}

PD_INLINE void pd_bw_write_signed(pd_bit_writer_t* w, int64_t value, int width,
                                  pd_endian_t endian, pd_bit_order_t order) {
    const uint64_t mask = (width == 64) ? ~(uint64_t)0 : (((uint64_t)1 << width) - 1);
    pd_bw_write_unsigned(w, (uint64_t)value & mask, width, endian, order);
}

PD_INLINE void pd_bw_write_bytes(pd_bit_writer_t* w, const uint8_t* src, size_t len) {
    if ((w->cursor % 8) != 0) { w->overflow = true; return; }
    if (w->cursor + len * 8 > w->capacity_bits) { w->overflow = true; return; }
    memcpy(w->data + (w->cursor / 8), src, len);
    w->cursor += len * 8;
}

/* ---- reader ------------------------------------------------------------------------------- */

typedef struct pd_bit_reader {
    const uint8_t* data;
    size_t size_bits;
    size_t cursor;
    bool underflow;
} pd_bit_reader_t;

PD_INLINE void pd_br_init(pd_bit_reader_t* r, const uint8_t* data, size_t size_bytes) {
    r->data = data;
    r->size_bits = size_bytes * 8;
    r->cursor = 0;
    r->underflow = false;
}

PD_INLINE size_t pd_br_bit_offset(const pd_bit_reader_t* r) { return r->cursor; }
PD_INLINE bool pd_br_underflowed(const pd_bit_reader_t* r) { return r->underflow; }
PD_INLINE bool pd_br_at_end(const pd_bit_reader_t* r) { return r->cursor >= r->size_bits; }
PD_INLINE void pd_br_skip(pd_bit_reader_t* r, size_t bits) { r->cursor += bits; }

PD_INLINE void pd_br_align_to(pd_bit_reader_t* r, size_t alignment_bits) {
    size_t mod;
    if (alignment_bits == 0) return;
    mod = r->cursor % alignment_bits;
    if (mod) r->cursor += (alignment_bits - mod);
}

PD_INLINE uint64_t pd_br_read_unsigned(pd_bit_reader_t* r, int width,
                                       pd_endian_t endian, pd_bit_order_t order) {
    int i;
    uint64_t value = 0;
    if (width <= 0 || width > 64) { r->underflow = true; return 0; }
    if (r->cursor + (size_t)width > r->size_bits) { r->underflow = true; return 0; }

    if (order == PD_BITS_MSB_FIRST && (r->cursor % 8) == 0 && (width % 8) == 0) {
        const int bytes = width / 8;
        const uint8_t* p = r->data + (r->cursor / 8);
        uint64_t result = 0;
        if (endian == PD_ENDIAN_BIG) {
            for (i = 0; i < bytes; ++i) result = (result << 8) | p[i];
        } else {
            for (i = bytes - 1; i >= 0; --i) result = (result << 8) | p[i];
        }
        r->cursor += (size_t)width;
        return result;
    }

    for (i = 0; i < width; ++i) {
        const size_t byte_index = r->cursor / 8;
        const int bit_index = 7 - (int)(r->cursor % 8);
        const uint64_t bit = (r->data[byte_index] >> bit_index) & 1u;
        if (order == PD_BITS_LSB_FIRST) value |= bit << i;
        else value = (value << 1) | bit;
        ++r->cursor;
    }
    return value;
}

PD_INLINE int64_t pd_br_read_signed(pd_bit_reader_t* r, int width,
                                    pd_endian_t endian, pd_bit_order_t order) {
    const uint64_t raw = pd_br_read_unsigned(r, width, endian, order);
    uint64_t sign_bit;
    uint64_t upper;
    if (width == 64) return (int64_t)raw;
    sign_bit = (uint64_t)1 << (width - 1);
    if ((raw & sign_bit) == 0) return (int64_t)raw;
    upper = ~(((uint64_t)1 << width) - 1);
    return (int64_t)(raw | upper);
}

PD_INLINE bool pd_br_read_bytes(pd_bit_reader_t* r, uint8_t* dst, size_t len) {
    if ((r->cursor % 8) != 0) { r->underflow = true; return false; }
    if (r->cursor + len * 8 > r->size_bits) { r->underflow = true; return false; }
    memcpy(dst, r->data + (r->cursor / 8), len);
    r->cursor += len * 8;
    return true;
}

#ifdef __cplusplus
}   /* extern "C" */
#endif

#endif /* PROTODESIGNER_RUNTIME_H */
""";
}
