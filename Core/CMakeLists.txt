cmake_minimum_required(VERSION 3.25)
project(SizeMonitorCore CXX ASM_MASM)

include(CTest)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# CMake's default MASM pipeline does not create the intermediate obj
# subdirectory for source files in subfolders, causing ml64 A1000.
# Drive assembly explicitly so the output lands in the binary root.
set(SIMD_OBJ "${CMAKE_CURRENT_BINARY_DIR}/simd_sum_asm.obj")
add_custom_command(
    OUTPUT  "${SIMD_OBJ}"
    COMMAND "${CMAKE_ASM_MASM_COMPILER}" /c /nologo
            /Fo "${SIMD_OBJ}"
            "${CMAKE_CURRENT_SOURCE_DIR}/asm/simd_sum.asm"
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/asm/simd_sum.asm"
    COMMENT "Assembling simd_sum.asm"
    VERBATIM
)
set_source_files_properties("${SIMD_OBJ}" PROPERTIES EXTERNAL_OBJECT TRUE GENERATED TRUE)
# Keep the .asm in the source list for IDE visibility; skip the auto-pipeline.
set_source_files_properties(asm/simd_sum.asm PROPERTIES HEADER_FILE_ONLY TRUE)

# Own the generated MASM output in exactly one build target. Both consumers use
# this target's object set, preventing parallel ml64 invocations on the same file.
add_library(simd_sum_obj STATIC asm/simd_sum.cpp "${SIMD_OBJ}")
target_include_directories(simd_sum_obj PRIVATE asm src)

add_library(Canopy.Core SHARED
    src/api.cpp
    src/scanner_router.cpp
    src/mft_scanner.cpp
    src/dir_scanner.cpp
    src/node_pool.cpp
    src/tree_compactor.cpp
    src/size_rollup.cpp
    src/cpu_features.cpp
    src/scan_options.cpp
    asm/simd_sum.asm
)
target_link_libraries(Canopy.Core PRIVATE simd_sum_obj)

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
target_compile_definitions(scan_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(scan_test PRIVATE /W4 /utf-8)
endif()
add_test(NAME native_scan_contract COMMAND scan_test "${CMAKE_CURRENT_SOURCE_DIR}/test")

add_executable(node_pool_test test/node_pool_test.cpp src/node_pool.cpp src/tree_compactor.cpp)
target_include_directories(node_pool_test PRIVATE include src)
target_compile_definitions(node_pool_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX SMON_NODE_POOL_TESTING)
if(MSVC)
    target_compile_options(node_pool_test PRIVATE /W4 /WX /utf-8)
endif()
add_test(NAME node_pool_contract COMMAND node_pool_test)

add_executable(simd_sum_test
    test/simd_sum_test.cpp
    src/cpu_features.cpp
)
target_link_libraries(simd_sum_test PRIVATE simd_sum_obj)
target_include_directories(simd_sum_test PRIVATE asm src)
target_compile_definitions(simd_sum_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(simd_sum_test PRIVATE /W4 /WX /utf-8)
endif()
add_test(NAME avx2_asm_parity COMMAND simd_sum_test)

add_executable(scan_options_test test/scan_options_test.cpp src/scan_options.cpp)
target_include_directories(scan_options_test PRIVATE src)
target_compile_definitions(scan_options_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(scan_options_test PRIVATE /W4 /WX /utf-8)
endif()
add_test(NAME scan_options_contract COMMAND scan_options_test)
