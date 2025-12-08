import os

# Fix UserDashboardController.cs
controller_path = r"c:\Users\admin\Desktop\Scope India Project\MessangerWeb\MessangerWeb\Controllers\UserDashboardController.cs"
with open(controller_path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

# Look for the specific pattern of extra brace around line 134
# Pattern:
#                         command.ExecuteNonQuery();
#                     }
#                     }
#                 }

for i in range(len(lines)):
    if "command.ExecuteNonQuery();" in lines[i]:
        # Check next few lines
        if i + 2 < len(lines) and "}" in lines[i+1] and "}" in lines[i+2]:
            # Found the double brace
            print(f"Found extra brace at line {i+3} in UserDashboardController.cs")
            # Remove one of them
            lines.pop(i+2)
            break

with open(controller_path, 'w', encoding='utf-8') as f:
    f.writelines(lines)

print("Fixed UserDashboardController.cs")

# Fix Program.cs
program_path = r"c:\Users\admin\Desktop\Scope India Project\MessangerWeb\MessangerWeb\Program.cs"
with open(program_path, 'r', encoding='utf-8') as f:
    p_lines = f.readlines()

# Look for the invalid block in Program.cs
# It seems I might have pasted the whole file content twice or something weird based on previous diffs?
# Or just a bad replace.
# The error was:
# #14 2.637 /source/MessangerWeb/Program.cs(109,26): error CS1026: ) expected
# This corresponds to the `endpoints.MapHub` section.

# Let's look for the specific bad block:
#                 endpoints.MapHub<ChatHub>("/chatHub");
#                     options.TransportMaxBufferSize = 1024 * 1024; // 1MB
#                     options.TransportSendTimeout = TimeSpan.FromSeconds(30);
#                 });
#             });

# It should be:
#                 endpoints.MapHub<ChatHub>("/chatHub", options =>
#                 {
#                     options.TransportMaxBufferSize = 1024 * 1024; // 1MB
#                     options.TransportSendTimeout = TimeSpan.FromSeconds(30);
#                 });

fixed_p_lines = []
skip = False
for i in range(len(p_lines)):
    if skip:
        skip = False
        continue
        
    line = p_lines[i]
    if 'endpoints.MapHub<ChatHub>("/chatHub");' in line:
        # Check if next line is options...
        if i + 1 < len(p_lines) and "options.TransportMaxBufferSize" in p_lines[i+1]:
            # Found the bad block
            print("Found bad ChatHub block in Program.cs")
            fixed_p_lines.append('                endpoints.MapHub<ChatHub>("/chatHub", options =>\n')
            fixed_p_lines.append('                {\n')
            # The next lines are options...
            # We need to preserve them but they are already there in next iterations?
            # No, I need to handle them.
            # Actually, I can just replace the line.
        else:
            fixed_p_lines.append(line)
    else:
        fixed_p_lines.append(line)

# Wait, the logic above is a bit flawed for multi-line fix.
# Let's just rewrite the file content if we find the bad pattern string.
p_content = "".join(p_lines)
bad_pattern = """                endpoints.MapHub<ChatHub>("/chatHub");
                    options.TransportMaxBufferSize = 1024 * 1024; // 1MB
                    options.TransportSendTimeout = TimeSpan.FromSeconds(30);
                });"""

good_pattern = """                endpoints.MapHub<ChatHub>("/chatHub", options =>
                {
                    options.TransportMaxBufferSize = 1024 * 1024; // 1MB
                    options.TransportSendTimeout = TimeSpan.FromSeconds(30);
                });"""

if bad_pattern in p_content:
    p_content = p_content.replace(bad_pattern, good_pattern)
    print("Fixed Program.cs")

with open(program_path, 'w', encoding='utf-8') as f:
    f.write(p_content)

print("Done")
