cmake_minimum_required(VERSION 3.25)
project(SizeMonitorCore CXX ASM_MASM)

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

add_library(Canopy.Core SHARED
    src/api.cpp
    src/scanner_router.cpp
    src/mft_scanner.cpp
    src/dir_scanner.cpp
    src/node_pool.cpp
    src/size_rollup.cpp
    asm/simd_sum.cpp
    asm/simd_sum.asm
    "${SIMD_OBJ}"
)

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
target_compile_definitions(scan_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(scan_test PRIVATE /W4 /utf-8)
endif()
