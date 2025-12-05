
import sys

def check_braces(filepath):
    with open(filepath, 'r') as f:
        lines = f.readlines()

    balance = 0
    for i, line in enumerate(lines):
        for char in line:
            if char == '{':
                balance += 1
            elif char == '}':
                balance -= 1
        
        if balance < 0:
            print(f"Brace balance went negative at line {i+1}: {line.strip()}")
            return

    if balance != 0:
        print(f"Final brace balance is {balance} (should be 0)")
    else:
        print("Braces are balanced.")

if __name__ == "__main__":
    check_braces(sys.argv[1])
