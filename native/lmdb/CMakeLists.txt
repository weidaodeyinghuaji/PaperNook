cmake_minimum_required(VERSION 3.21)

project(PaperTodoLmdb LANGUAGES C)

if(NOT WIN32)
  message(FATAL_ERROR "PaperTodo's LMDB binary is built for Windows only.")
endif()

add_library(papertodo_lmdb SHARED
  upstream/mdb.c
  upstream/midl.c
  papertodo_lmdb.def)

target_include_directories(papertodo_lmdb PRIVATE upstream)
target_compile_features(papertodo_lmdb PRIVATE c_std_11)
target_compile_definitions(papertodo_lmdb PRIVATE
  _CRT_SECURE_NO_WARNINGS)

if(MSVC)
  set_property(TARGET papertodo_lmdb PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  target_compile_options(papertodo_lmdb PRIVATE
    $<$<CONFIG:Release>:/O1>
    /Gy)
  target_link_options(papertodo_lmdb PRIVATE
    /OPT:REF
    /OPT:ICF)
endif()

set_target_properties(papertodo_lmdb PROPERTIES
  OUTPUT_NAME "papertodo_lmdb"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/bin/win-x64")

foreach(configuration Debug Release RelWithDebInfo MinSizeRel)
  string(TOUPPER "${configuration}" configuration_upper)
  set_target_properties(papertodo_lmdb PROPERTIES
    "RUNTIME_OUTPUT_DIRECTORY_${configuration_upper}"
    "${CMAKE_CURRENT_SOURCE_DIR}/bin/win-x64")
endforeach()
