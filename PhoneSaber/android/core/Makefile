CXX = clang++
PYTHON ?= python3.12
CXXFLAGS = -std=c++17 -O2 -Wall -Wextra -Werror -ffp-contract=off
BUILD ?= /tmp/phonesaber-cpp-core
SOURCES = src/detection.cpp src/pipeline.cpp src/frame_processor.cpp
HEADERS = include/phonesaber/core.hpp src/internal.hpp

.PHONY: all benchmark parity test clean
all: $(BUILD)/phonesaber-png $(BUILD)/core-test
benchmark: $(BUILD)/core-benchmark
$(BUILD):
	mkdir -p "$@"
$(BUILD)/phonesaber-png: $(SOURCES) $(HEADERS) tools/png_cli.cpp tools/png.cpp tools/png.hpp | $(BUILD)
	$(CXX) $(CXXFLAGS) -Iinclude $(SOURCES) tools/png_cli.cpp tools/png.cpp -lz -o "$@"
$(BUILD)/core-test: $(SOURCES) $(HEADERS) tools/core_test.cpp | $(BUILD)
	$(CXX) $(CXXFLAGS) -Iinclude $(SOURCES) tools/core_test.cpp -o "$@"
test: all
	"$(BUILD)/core-test"
	$(PYTHON) -B -m unittest discover -s tools -p 'test_*.py' -v
$(BUILD)/core-benchmark: $(SOURCES) $(HEADERS) tools/benchmark.cpp tools/png.cpp tools/png.hpp | $(BUILD)
	$(CXX) $(CXXFLAGS) -Iinclude $(SOURCES) tools/benchmark.cpp tools/png.cpp -lz -o "$@"
parity:
	$(PYTHON) -B tools/compare_parity.py
clean:
	rm -f "$(BUILD)/phonesaber-png" "$(BUILD)/core-test" "$(BUILD)/core-benchmark"
