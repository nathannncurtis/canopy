cmake_minimum_required(VERSION 3.25)
project(SizeMonitorCore CXX)

option(SMON_ENABLE_AVX2_SUM "Enable measured AVX2 raw-assembly sum dispatch" OFF)

include(CTest)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(SMON_X64_ASM OFF)
if(MSVC AND (CMAKE_GENERATOR_PLATFORM STREQUAL "x64" OR CMAKE_VS_PLATFORM_NAME STREQUAL "x64"))
    set(SMON_X64_ASM ON)
    enable_language(ASM_MASM)
    # Drive x64 MASM explicitly so parallel targets share one generated object.
    set(SIMD_OBJ "${CMAKE_CURRENT_BINARY_DIR}/simd_sum_asm.obj")
    add_custom_command(
        OUTPUT  "${SIMD_OBJ}"
        COMMAND "${CMAKE_ASM_MASM_COMPILER}" /c /nologo
                /Fo "${SIMD_OBJ}"
                "${CMAKE_CURRENT_SOURCE_DIR}/asm/simd_sum.asm"
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/asm/simd_sum.asm"
        COMMENT "Assembling measured x64 AVX2 sum"
        VERBATIM
    )
    set_source_files_properties("${SIMD_OBJ}" PROPERTIES EXTERNAL_OBJECT TRUE GENERATED TRUE)
    set_source_files_properties(asm/simd_sum.asm PROPERTIES HEADER_FILE_ONLY TRUE)
endif()
if(SMON_ENABLE_AVX2_SUM AND NOT SMON_X64_ASM)
    message(STATUS "SMON_ENABLE_AVX2_SUM ignored: x64 MASM is unavailable for ${CMAKE_GENERATOR_PLATFORM}")
endif()
message(STATUS "Canopy native architecture=${CMAKE_GENERATOR_PLATFORM}; x64 MASM=${SMON_X64_ASM}")

# Own the generated MASM output in exactly one build target. Both consumers use
# this target's object set, preventing parallel ml64 invocations on the same file.
add_library(simd_sum_obj STATIC asm/simd_sum.cpp)
if(SMON_X64_ASM)
    target_sources(simd_sum_obj PRIVATE "${SIMD_OBJ}" asm/simd_sum.asm)
    target_compile_definitions(simd_sum_obj PRIVATE SMON_HAS_X64_ASM=1)
endif()
target_include_directories(simd_sum_obj PRIVATE asm src)

add_library(Canopy.Core SHARED
    src/api.cpp
    src/scanner_router.cpp
    src/mft_scanner.cpp
    src/dir_scanner.cpp
    src/node_pool.cpp
    src/tree_compactor.cpp
    src/traversal_policy.cpp
    src/filesystem_policy.cpp
    src/network_scan_policy.cpp
    src/size_rollup.cpp
    src/cpu_features.cpp
    src/architecture_dispatch.cpp
    src/scan_options.cpp
)
target_link_libraries(Canopy.Core PRIVATE simd_sum_obj)
target_link_libraries(Canopy.Core PRIVATE Mpr)

target_include_directories(Canopy.Core PRIVATE include)

target_compile_definitions(Canopy.Core PRIVATE
    SMON_CORE_EXPORTS
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

if(MSVC)
    target_compile_options(Canopy.Core PRIVATE /W4 /WX /utf-8)
    target_compile_options(Canopy.Core PRIVATE
        $<$<CONFIG:Release>:/O2>
    )
endif()

set_target_properties(Canopy.Core PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
)

add_executable(scan_test test/scan_test.cpp)
target_include_directories(scan_test PRIVATE include)
target_link_libraries(scan_test PRIVATE Canopy.Core)
add_custom_command(TARGET scan_test POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
            "$<TARGET_FILE:Canopy.Core>"
            "$<TARGET_FILE_DIR:scan_test>"
    COMMENT "Copying Canopy.Core.dll beside scan_test"
)
if(SMON_X64_ASM)
    target_compile_definitions(Canopy.Core PRIVATE SMON_HAS_X64_ASM=1)
endif()
if(SMON_ENABLE_AVX2_SUM AND SMON_X64_ASM)
    target_compile_definitions(Canopy.Core PRIVATE SMON_ENABLE_AVX2_SUM=1)
endif()
target_compile_definitions(scan_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(scan_test PRIVATE /W4 /utf-8)
endif()
add_test(NAME native_scan_contract COMMAND scan_test "${CMAKE_CURRENT_SOURCE_DIR}/test")

add_executable(filesystem_corpus_test test/filesystem_corpus_test.cpp)
target_include_directories(filesystem_corpus_test PRIVATE include)
target_link_libraries(filesystem_corpus_test PRIVATE Canopy.Core)
add_custom_command(TARGET filesystem_corpus_test POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
            "$<TARGET_FILE:Canopy.Core>"
            "$<TARGET_FILE_DIR:filesystem_corpus_test>"
    COMMENT "Copying Canopy.Core.dll beside filesystem_corpus_test"
)
target_compile_definitions(filesystem_corpus_test PRIVATE
    UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(filesystem_corpus_test PRIVATE /W4 /WX /utf-8)
endif()
add_test(NAME filesystem_correctness_corpus COMMAND filesystem_corpus_test)

add_executable(node_pool_test test/node_pool_test.cpp src/node_pool.cpp src/tree_compactor.cpp)
target_include_directories(node_pool_test PRIVATE include src)
target_compile_definitions(node_pool_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX SMON_NODE_POOL_TESTING)
if(MSVC)
    target_compile_options(node_pool_test PRIVATE /W4 /WX /utf-8)
endif()
add_test(NAME node_pool_contract COMMAND node_pool_test)

if(SMON_X64_ASM)
    add_executable(simd_sum_test test/simd_sum_test.cpp src/cpu_features.cpp)
    target_link_libraries(simd_sum_test PRIVATE simd_sum_obj)
    target_include_directories(simd_sum_test PRIVATE asm src)
    target_compile_definitions(simd_sum_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX SMON_HAS_X64_ASM=1)
    if(SMON_ENABLE_AVX2_SUM)
        target_compile_definitions(simd_sum_test PRIVATE SMON_ENABLE_AVX2_SUM=1)
    endif()
    if(MSVC)
        target_compile_options(simd_sum_test PRIVATE /W4 /WX /utf-8)
    endif()
    add_test(NAME avx2_asm_parity COMMAND simd_sum_test)
endif()

add_executable(architecture_sum_test test/architecture_sum_test.cpp
    src/architecture_dispatch.cpp src/cpu_features.cpp)
target_link_libraries(architecture_sum_test PRIVATE simd_sum_obj)
target_include_directories(architecture_sum_test PRIVATE asm src)
target_compile_definitions(architecture_sum_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(SMON_X64_ASM)
    target_compile_definitions(architecture_sum_test PRIVATE SMON_HAS_X64_ASM=1)
endif()
if(SMON_ENABLE_AVX2_SUM AND SMON_X64_ASM)
    target_compile_definitions(architecture_sum_test PRIVATE SMON_ENABLE_AVX2_SUM=1)
endif()
if(MSVC)
    target_compile_options(architecture_sum_test PRIVATE /W4 /WX /utf-8)
endif()
add_test(NAME architecture_sum_parity COMMAND architecture_sum_test)

add_executable(scan_options_test test/scan_options_test.cpp src/scan_options.cpp)
target_include_directories(scan_options_test PRIVATE src)
target_compile_definitions(scan_options_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(scan_options_test PRIVATE /W4 /WX /utf-8)
endif()
add_test(NAME scan_options_contract COMMAND scan_options_test)

add_executable(traversal_policy_test test/traversal_policy_test.cpp src/traversal_policy.cpp)
target_include_directories(traversal_policy_test PRIVATE include src)
target_link_libraries(traversal_policy_test PRIVATE Canopy.Core)
add_custom_command(TARGET traversal_policy_test POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
            "$<TARGET_FILE:Canopy.Core>" "$<TARGET_FILE_DIR:traversal_policy_test>")
target_compile_definitions(traversal_policy_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(traversal_policy_test PRIVATE /W4 /WX /utf-8)
endif()
add_test(NAME traversal_policy_contract COMMAND traversal_policy_test)

add_executable(filesystem_policy_test test/filesystem_policy_test.cpp src/filesystem_policy.cpp)
target_include_directories(filesystem_policy_test PRIVATE src)
target_compile_definitions(filesystem_policy_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(filesystem_policy_test PRIVATE /W4 /WX /utf-8)
endif()
add_test(NAME filesystem_policy_contract COMMAND filesystem_policy_test)

add_executable(network_scan_policy_test test/network_scan_policy_test.cpp src/network_scan_policy.cpp)
target_include_directories(network_scan_policy_test PRIVATE src)
target_compile_definitions(network_scan_policy_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(network_scan_policy_test PRIVATE /W4 /WX /utf-8)
endif()
add_test(NAME network_scan_policy_contract COMMAND network_scan_policy_test)
