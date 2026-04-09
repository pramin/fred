#!/bin/bash

# Comprehensive newline compatibility test between ned and GNU sed
# This script demonstrates that ned now matches GNU sed exactly for all newline edge cases

echo "=== NED vs GNU SED: Newline Formatting Compatibility Test ==="
echo ""

# Build ned if needed
if [ ! -f "bin/Debug/net8.0/ned" ]; then
    echo "Building ned..."
    dotnet build -q
    echo ""
fi

test_count=0
pass_count=0

# Test function that compares ned vs GNU sed byte-for-byte
test_newline_case() {
    local input_desc="$1"
    local input_data="$2"  
    local script="$3"
    local description="$4"
    
    test_count=$((test_count + 1))
    echo "Test $test_count: $description"
    echo "Input: $input_desc"
    echo "Script: $script"
    
    # Create temp files for comparison
    local gnu_file=$(mktemp)
    local ned_file=$(mktemp)
    
    # Run both tools and capture output
    printf "$input_data" | sed "$script" > "$gnu_file"
    printf "$input_data" | ./bin/Debug/net8.0/ned "$script" > "$ned_file"
    
    # Compare byte-for-byte
    if cmp -s "$gnu_file" "$ned_file"; then
        echo "✓ PASS: Byte-perfect match"
        pass_count=$((pass_count + 1))
    else
        echo "✗ FAIL: Output differs"
        echo "GNU sed hex: $(hexdump -C "$gnu_file" | head -1)"
        echo "ned hex:     $(hexdump -C "$ned_file" | head -1)"
    fi
    
    # Show hex output for verification
    local gnu_hex=$(hexdump -C "$gnu_file" | head -1 | cut -d' ' -f2-9 | tr -d ' ')
    local ned_hex=$(hexdump -C "$ned_file" | head -1 | cut -d' ' -f2-9 | tr -d ' ')
    echo "Hex output: $gnu_hex"
    
    # Cleanup
    rm -f "$gnu_file" "$ned_file"
    echo ""
}

# Test Case 1: Basic line with newline (echo "test")
test_newline_case "echo \"test\" (with newline)" "test\n" "s/test/REPLACED/" "Basic substitution with trailing newline"

# Test Case 2: Basic line without newline (printf "test")  
test_newline_case "printf \"test\" (no newline)" "test" "s/test/REPLACED/" "Basic substitution without trailing newline"

# Test Case 3: Empty line (echo "")
test_newline_case "echo \"\" (empty line with newline)" "\n" "p" "Empty line with print command"

# Test Case 4: Multi-line input
test_newline_case "Two lines with newlines" "line1\nline2\n" "p" "Multi-line print command"

# Test Case 5: Multi-line input without final newline
test_newline_case "Two lines, no final newline" "line1\nline2" "p" "Multi-line without final newline"

# Test Case 6: Pattern matching print
test_newline_case "Pattern match test" "match\nno\nmatch\n" "/match/p" "Pattern matching with print"

# Test Case 7: Delete command
test_newline_case "Delete test" "keep\ndelete\nkeep\n" "2d" "Line deletion"

# Test Case 8: Substitute with print flag
test_newline_case "Substitute with print" "test line\n" "s/test/BEST/p" "Substitute with print flag"

# Test Case 9: Global substitute
test_newline_case "Global substitute" "test test test\n" "s/test/X/g" "Global substitution"

# Test Case 10: No newline input, no match
test_newline_case "No match, no newline" "nomatch" "s/test/X/" "No substitution, no newline preserved"

echo "=== TEST SUMMARY ==="
echo "Total tests: $test_count"
echo "Passed: $pass_count"
echo "Failed: $((test_count - pass_count))"

if [ $pass_count -eq $test_count ]; then
    echo ""
    echo "🎉 ALL TESTS PASSED! 🎉"
    echo "ned now has byte-perfect newline compatibility with GNU sed."
    echo "The newline formatting issue has been completely resolved!"
else
    echo ""
    echo "❌ Some tests failed. Newline compatibility needs more work."
    exit 1
fi