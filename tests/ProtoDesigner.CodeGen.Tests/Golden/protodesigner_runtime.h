// -----------------------------------------------------------------------------
// ProtoDesigner runtime — generated. Do not edit.
//
// A minimal bit-level cursor over a byte buffer. Sub-byte fields pack MSB-first
// within their storage byte; whole-byte fields honour the endianness passed in.
// C++14, no dynamic allocation, no exceptions, header-only.
// -----------------------------------------------------------------------------
#ifndef PROTODESIGNER_RUNTIME_H
#define PROTODESIGNER_RUNTIME_H

#include <stdint.h>
#include <stddef.h>
#include <string.h>

namespace protodesigner {

enum class Endian { Little, Big };

// Result of a decode attempt. `ok` is false when the buffer ran out.
// Named DecodeResult rather than Status so it can never collide with a user message called
// "Status" — protocol authors use that name constantly.
struct DecodeResult {
    bool ok;
    size_t bit_offset;   // where the failure happened, when !ok
    static DecodeResult Ok() { return DecodeResult{true, 0}; }
    static DecodeResult Fail(size_t at) { return DecodeResult{false, at}; }
};

// Turns a scaled value into a wire code. Rounds to nearest, half away from zero — NOT the truncation
// a plain cast to an integer would do. Truncating biases every reading downward by up to one whole
// code and makes the top of the range unreachable: a -40..70 temperature scaled by 110/255 arrives at
// 254.99999999999997 for 70.0, which would truncate to 254 and never reach the 255 the width allows.
inline int64_t quantize(double v) {
    return static_cast<int64_t>(v < 0.0 ? v - 0.5 : v + 0.5);
}

// Reinterprets a float's bits as an integer and back, for fields sent as raw IEEE-754. memcpy is the
// only strictly-conforming route in C++14 — a union or a pointer cast is undefined behaviour, and
// compilers optimise this away to nothing.
inline uint32_t float_bits(float v) {
    uint32_t bits = 0;
    memcpy(&bits, &v, sizeof(bits));
    return bits;
}

inline float bits_to_float(uint32_t bits) {
    float v = 0.0f;
    memcpy(&v, &bits, sizeof(v));
    return v;
}

inline uint64_t double_bits(double v) {
    uint64_t bits = 0;
    memcpy(&bits, &v, sizeof(bits));
    return bits;
}

inline double bits_to_double(uint64_t bits) {
    double v = 0.0;
    memcpy(&v, &bits, sizeof(v));
    return v;
}

class BitWriter {
public:
    BitWriter(uint8_t* data, size_t capacity_bytes)
        : data_(data), capacity_bits_(capacity_bytes * 8), cursor_(0), overflow_(false) {
        if (data_ && capacity_bytes) memset(data_, 0, capacity_bytes);
    }

    size_t bit_length() const { return cursor_; }
    size_t byte_length() const { return (cursor_ + 7) / 8; }
    bool overflowed() const { return overflow_; }

    void skip(size_t bits) { cursor_ += bits; }

    void pad_to(size_t alignment_bits) {
        if (alignment_bits == 0) return;
        size_t mod = cursor_ % alignment_bits;
        if (mod) cursor_ += (alignment_bits - mod);
    }

    void write_unsigned(uint64_t value, int width, Endian endian) {
        if (width <= 0 || width > 64) { overflow_ = true; return; }
        if (cursor_ + static_cast<size_t>(width) > capacity_bits_) { overflow_ = true; return; }

        // Fast path: byte-aligned cursor and whole-byte width.
        if ((cursor_ % 8) == 0 && (width % 8) == 0) {
            const int bytes = width / 8;
            uint8_t* p = data_ + (cursor_ / 8);
            if (endian == Endian::Big) {
                for (int i = 0; i < bytes; ++i)
                    p[i] = static_cast<uint8_t>((value >> ((bytes - 1 - i) * 8)) & 0xFFu);
            } else {
                for (int i = 0; i < bytes; ++i)
                    p[i] = static_cast<uint8_t>((value >> (i * 8)) & 0xFFu);
            }
            cursor_ += static_cast<size_t>(width);
            return;
        }

        // Slow path: bit by bit, MSB-first inside each byte.
        for (int i = width - 1; i >= 0; --i) {
            const uint64_t bit = (value >> i) & 1ull;
            if (bit) {
                const size_t byte_index = cursor_ / 8;
                const int bit_index = 7 - static_cast<int>(cursor_ % 8);
                data_[byte_index] |= static_cast<uint8_t>(1u << bit_index);
            }
            ++cursor_;
        }
    }

    void write_signed(int64_t value, int width, Endian endian) {
        const uint64_t mask = (width == 64) ? ~0ull : ((1ull << width) - 1ull);
        write_unsigned(static_cast<uint64_t>(value) & mask, width, endian);
    }

    void write_bytes(const uint8_t* src, size_t len) {
        if ((cursor_ % 8) != 0) { overflow_ = true; return; }
        if (cursor_ + len * 8 > capacity_bits_) { overflow_ = true; return; }
        memcpy(data_ + (cursor_ / 8), src, len);
        cursor_ += len * 8;
    }

private:
    uint8_t* data_;
    size_t capacity_bits_;
    size_t cursor_;
    bool overflow_;
};

class BitReader {
public:
    BitReader(const uint8_t* data, size_t size_bytes)
        : data_(data), size_bits_(size_bytes * 8), cursor_(0), underflow_(false) {}

    size_t bit_offset() const { return cursor_; }
    bool underflowed() const { return underflow_; }
    bool at_end() const { return cursor_ >= size_bits_; }

    void skip(size_t bits) { cursor_ += bits; }

    void align_to(size_t alignment_bits) {
        if (alignment_bits == 0) return;
        size_t mod = cursor_ % alignment_bits;
        if (mod) cursor_ += (alignment_bits - mod);
    }

    uint64_t read_unsigned(int width, Endian endian) {
        if (width <= 0 || width > 64) { underflow_ = true; return 0; }
        if (cursor_ + static_cast<size_t>(width) > size_bits_) { underflow_ = true; return 0; }

        if ((cursor_ % 8) == 0 && (width % 8) == 0) {
            const int bytes = width / 8;
            const uint8_t* p = data_ + (cursor_ / 8);
            uint64_t result = 0;
            if (endian == Endian::Big) {
                for (int i = 0; i < bytes; ++i) result = (result << 8) | p[i];
            } else {
                for (int i = bytes - 1; i >= 0; --i) result = (result << 8) | p[i];
            }
            cursor_ += static_cast<size_t>(width);
            return result;
        }

        uint64_t value = 0;
        for (int i = 0; i < width; ++i) {
            const size_t byte_index = cursor_ / 8;
            const int bit_index = 7 - static_cast<int>(cursor_ % 8);
            const uint64_t bit = (data_[byte_index] >> bit_index) & 1u;
            value = (value << 1) | bit;
            ++cursor_;
        }
        return value;
    }

    int64_t read_signed(int width, Endian endian) {
        const uint64_t raw = read_unsigned(width, endian);
        if (width == 64) return static_cast<int64_t>(raw);
        const uint64_t sign_bit = 1ull << (width - 1);
        if ((raw & sign_bit) == 0) return static_cast<int64_t>(raw);
        const uint64_t upper = ~((1ull << width) - 1ull);
        return static_cast<int64_t>(raw | upper);
    }

    bool read_bytes(uint8_t* dst, size_t len) {
        if ((cursor_ % 8) != 0) { underflow_ = true; return false; }
        if (cursor_ + len * 8 > size_bits_) { underflow_ = true; return false; }
        memcpy(dst, data_ + (cursor_ / 8), len);
        cursor_ += len * 8;
        return true;
    }

private:
    const uint8_t* data_;
    size_t size_bits_;
    size_t cursor_;
    bool underflow_;
};

} // namespace protodesigner

#endif // PROTODESIGNER_RUNTIME_H