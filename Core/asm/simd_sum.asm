; uint64_t SmonSumU64_AVX2(const uint64_t* data, uint32_t count)
; Windows x64 ABI: RCX=data, EDX=count, returns RAX.
; All registers clobbered here are volatile in the Windows x64 ABI.

.code

SmonSumU64_AVX2 PROC
        vpxor   ymm0, ymm0, ymm0
        vpxor   ymm1, ymm1, ymm1
        vpxor   ymm2, ymm2, ymm2
        vpxor   ymm3, ymm3, ymm3
        xor     r8d, r8d

        ; 4-accumulator unrolled loop: 16 elements per iteration.
        mov     r9d, edx
        and     r9d, 0FFFFFFF0h
        cmp     r8d, r9d
        jae     MergeAccums

Unroll16:
        vmovdqu ymm4, YMMWORD PTR [rcx + r8*8]
        vmovdqu ymm5, YMMWORD PTR [rcx + r8*8 + 32]
        vmovdqu ymm6, YMMWORD PTR [rcx + r8*8 + 64]
        vmovdqu ymm7, YMMWORD PTR [rcx + r8*8 + 96]
        vpaddq  ymm0, ymm0, ymm4
        vpaddq  ymm1, ymm1, ymm5
        vpaddq  ymm2, ymm2, ymm6
        vpaddq  ymm3, ymm3, ymm7
        add     r8d, 16
        cmp     r8d, r9d
        jb      Unroll16
        vpaddq  ymm0, ymm0, ymm1
        vpaddq  ymm2, ymm2, ymm3
        vpaddq  ymm0, ymm0, ymm2

MergeAccums:
        ; Tail: groups of 4 remaining elements.
        mov     r9d, edx
        sub     r9d, r8d
        and     r9d, 0FFFFFFFCh
        add     r9d, r8d
        cmp     r8d, r9d
        jae     HorizReduce

Tail4:
        vmovdqu ymm4, YMMWORD PTR [rcx + r8*8]
        vpaddq  ymm0, ymm0, ymm4
        add     r8d, 4
        cmp     r8d, r9d
        jb      Tail4

HorizReduce:
        vextracti128 xmm1, ymm0, 1
        vpaddq       xmm0, xmm0, xmm1
        vpunpckhqdq  xmm1, xmm0, xmm0
        vpaddq       xmm0, xmm0, xmm1
        vmovq        rax, xmm0
        cmp          r8d, edx
        jae          SumDone

ScalarTail:
        add     rax, QWORD PTR [rcx + r8*8]
        inc     r8d
        cmp     r8d, edx
        jb      ScalarTail

SumDone:
        vzeroupper
        ret
SmonSumU64_AVX2 ENDP

END
