# Two independently linked MinHook copies model a foreign driver's separate DLL state.
add_library(foreign_fixture_minhook STATIC
  ${PROJECT_SOURCE_DIR}/third_party/minhook/src/buffer.c
  ${PROJECT_SOURCE_DIR}/third_party/minhook/src/hook.c
  ${PROJECT_SOURCE_DIR}/third_party/minhook/src/trampoline.c
  ${PROJECT_SOURCE_DIR}/third_party/minhook/src/hde/hde64.c)
target_include_directories(foreign_fixture_minhook PUBLIC ${PROJECT_SOURCE_DIR}/third_party/minhook/include)
add_library(switcheroonie-foreign-fixture SHARED fixture.cpp)
target_include_directories(switcheroonie-foreign-fixture SYSTEM PRIVATE ${PROJECT_SOURCE_DIR}/third_party/openvr/headers)
target_link_libraries(switcheroonie-foreign-fixture PRIVATE foreign_fixture_minhook)
add_executable(switcheroonie-foreign-chain-tests fixture_tests.cpp ${PROJECT_SOURCE_DIR}/native/fake_calls.cpp)
target_include_directories(switcheroonie-foreign-chain-tests PRIVATE ${PROJECT_SOURCE_DIR}/native)
target_link_libraries(switcheroonie-foreign-chain-tests PRIVATE switcheroonie_hooks)
add_dependencies(switcheroonie-foreign-chain-tests switcheroonie-foreign-fixture)
if(MSVC)
  target_compile_options(switcheroonie-foreign-fixture PRIVATE /W4 /permissive- /EHsc)
  target_compile_options(switcheroonie-foreign-chain-tests PRIVATE /W4 /permissive- /EHsc)
endif()
add_test(NAME isolated-foreign-calibration-chain COMMAND switcheroonie-foreign-chain-tests $<TARGET_FILE:switcheroonie-foreign-fixture>)
set_tests_properties(isolated-foreign-calibration-chain PROPERTIES TIMEOUT 30)
foreach(scenario tick submit input)
  add_test(NAME isolated-foreign-cleanup-${scenario}
    COMMAND switcheroonie-foreign-chain-tests $<TARGET_FILE:switcheroonie-foreign-fixture> ${scenario})
  set_tests_properties(isolated-foreign-cleanup-${scenario} PROPERTIES TIMEOUT 30)
endforeach()
