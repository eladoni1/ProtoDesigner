// Compiles the ProtoDesigner-generated headers and checks that encode/decode round-trips and that
// the produced bytes match what the C# reference codec produced for the same inputs.
#include <stdio.h>
#include <string.h>

#include "packed-bits.h"
#include "dynamic-array.h"
#include "scalars.h"
#include "struct-and-array.h"

static int g_failures = 0;

static void check(bool condition, const char* what) {
    if (!condition) {
        printf("FAIL: %s\n", what);
        ++g_failures;
    }
}

static void check_bytes(const uint8_t* got, size_t got_len,
                        const uint8_t* want, size_t want_len, const char* what) {
    if (got_len != want_len) {
        printf("FAIL: %s — length %zu, expected %zu\n", what, got_len, want_len);
        ++g_failures;
        return;
    }
    for (size_t i = 0; i < want_len; ++i) {
        if (got[i] != want[i]) {
            printf("FAIL: %s — byte %zu is 0x%02X, expected 0x%02X\n", what, i, got[i], want[i]);
            ++g_failures;
            return;
        }
    }
}

int main() {
    // ---- packed bits: 4-bit enum + range-compressed 4-bit value share one byte ----
    {
        proto::Status msg{};
        msg.mode = proto::Mode::Fault;      // 10
        msg.temperature = 1006;             // wire code 6
        msg.checksum = 0xBEEF;

        uint8_t buf[proto::Status::kMaxBytes];
        size_t n = proto::encode(msg, buf, sizeof(buf));
        check(n == 3, "packed: encodes to 3 bytes");
        // High nibble 0xA (Fault), low nibble 0x6 (1006-1000). Then checksum little-endian.
        const uint8_t want[] = { 0xA6, 0xEF, 0xBE };
        check_bytes(buf, n, want, sizeof(want), "packed: byte pattern matches the reference codec");

        proto::Status back{};
        auto st = proto::decode(buf, n, back);
        check(st.ok, "packed: decode succeeds");
        check(back.mode == proto::Mode::Fault, "packed: mode round-trips");
        check(back.temperature == 1006, "packed: temperature round-trips through the transform");
        check(back.checksum == 0xBEEF, "packed: checksum round-trips");
    }

    // ---- transform across its whole declared range ----
    {
        for (uint16_t t = 1000; t <= 1015; ++t) {
            proto::Status msg{};
            msg.mode = proto::Mode::Idle;
            msg.temperature = t;
            msg.checksum = 0;

            uint8_t buf[proto::Status::kMaxBytes];
            size_t n = proto::encode(msg, buf, sizeof(buf));

            proto::Status back{};
            proto::decode(buf, n, back);
            if (back.temperature != t) {
                printf("FAIL: transform round-trip broke at %u (got %u)\n", t, back.temperature);
                ++g_failures;
                break;
            }
        }
    }

    // ---- scalars, including a big-endian field ----
    {
        proto::Reading msg{};
        msg.id = 200;
        msg.sequence = 0x1234;   // big-endian on the wire
        msg.delta = -12345;

        uint8_t buf[proto::Reading::kMaxBytes];
        size_t n = proto::encode(msg, buf, sizeof(buf));
        check(buf[1] == 0x12 && buf[2] == 0x34, "scalars: big-endian field is byte-swapped");

        proto::Reading back{};
        auto st = proto::decode(buf, n, back);
        check(st.ok, "scalars: decode succeeds");
        check(back.id == 200, "scalars: u8 round-trips");
        check(back.sequence == 0x1234, "scalars: big-endian u16 round-trips");
        check(back.delta == -12345, "scalars: signed i32 round-trips");
    }

    // ---- struct flattening + static array ----
    {
        proto::Frame msg{};
        msg.header_messageId = 7;
        msg.header_timestamp = 123456789u;
        for (int i = 0; i < 4; ++i) msg.samples[i] = (uint16_t)(i + 1);

        uint8_t buf[proto::Frame::kMaxBytes];
        size_t n = proto::encode(msg, buf, sizeof(buf));

        proto::Frame back{};
        auto st = proto::decode(buf, n, back);
        check(st.ok, "composite: decode succeeds");
        check(back.header_messageId == 7, "composite: struct member round-trips");
        check(back.header_timestamp == 123456789u, "composite: nested u32 round-trips");
        bool samples_ok = true;
        for (int i = 0; i < 4; ++i) if (back.samples[i] != (uint16_t)(i + 1)) samples_ok = false;
        check(samples_ok, "composite: static array round-trips");
    }

    // ---- dynamic array at several lengths; trailing field must survive ----
    {
        const uint32_t lengths[] = { 0, 1, 5, 32 };
        for (size_t li = 0; li < sizeof(lengths) / sizeof(lengths[0]); ++li) {
            const uint32_t count = lengths[li];

            proto::Batch msg{};
            msg.count = (uint8_t)count;
            for (uint32_t i = 0; i < count; ++i) msg.payload[i] = (uint8_t)(i % 251);
            msg.crc = 0xCAFE;

            uint8_t buf[proto::Batch::kMaxBytes];
            size_t n = proto::encode(msg, buf, sizeof(buf));

            // 1 byte count + count bytes payload + 2 bytes crc
            if (n != (size_t)(1 + count + 2)) {
                printf("FAIL: dynamic: length %u encoded to %zu bytes, expected %u\n",
                       count, n, 1 + count + 2);
                ++g_failures;
            }

            proto::Batch back{};
            auto st = proto::decode(buf, n, back);
            if (!st.ok) { printf("FAIL: dynamic: decode failed at length %u\n", count); ++g_failures; continue; }
            if (back.count != count) { printf("FAIL: dynamic: count %u != %u\n", back.count, count); ++g_failures; }
            bool payload_ok = true;
            for (uint32_t i = 0; i < count; ++i)
                if (back.payload[i] != (uint8_t)(i % 251)) payload_ok = false;
            if (!payload_ok) { printf("FAIL: dynamic: payload mismatch at length %u\n", count); ++g_failures; }
            // The point of the region model: the field AFTER the variable part must still land right.
            if (back.crc != 0xCAFE) { printf("FAIL: dynamic: crc after variable region is 0x%04X at length %u\n", back.crc, count); ++g_failures; }
        }
    }

    if (g_failures == 0) {
        printf("ALL C++ TESTS PASSED\n");
        return 0;
    }
    printf("%d C++ CHECK(S) FAILED\n", g_failures);
    return 1;
}
