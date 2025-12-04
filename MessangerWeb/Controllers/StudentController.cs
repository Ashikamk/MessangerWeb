using System.Data;
using MessangerWeb.Models;
using Microsoft.AspNetCore.Mvc;
using MySql.Data.MySqlClient;
using Microsoft.Extensions.Configuration;

namespace MessangerWeb.Controllers
{
    public class StudentController : Controller
    {
        private readonly string connectionString;

        public StudentController(IConfiguration configuration)
        {
            connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        // GET: /Student/Add
        public IActionResult Add()
        {
            SetDropdowns();
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Add(Student model, IFormFile PhotoFile, string[] SelectedHobbies)
        {
            SetDropdowns();

            // Remove Photo and Hobbies from ModelState validation
            ModelState.Remove("Photo");
            ModelState.Remove("Hobbies");

            // Debug: Log the incoming model data
            Console.WriteLine($"=== ADD STUDENT DEBUG ===");
            Console.WriteLine($"FirstName: {model.FirstName}");
            Console.WriteLine($"LastName: {model.LastName}");
            Console.WriteLine($"Email: {model.Email}");
            Console.WriteLine($"Password: {model.Password}");
            Console.WriteLine($"SelectedHobbies: {(SelectedHobbies != null ? string.Join(", ", SelectedHobbies) : "null")}");

            if (!ModelState.IsValid)
            {
                Console.WriteLine($"ModelState is invalid:");
                foreach (var error in ModelState)
                {
                    if (error.Value.Errors.Count > 0)
                    {
                        Console.WriteLine($"{error.Key}: {string.Join(", ", error.Value.Errors.Select(e => e.ErrorMessage))}");
                    }
                }
                return View(model);
            }

            // Combine hobbies
            model.Hobbies = string.Join(", ", SelectedHobbies ?? Array.Empty<string>());

            // Convert photo to byte[]
            if (PhotoFile != null && PhotoFile.Length > 0)
            {
                Console.WriteLine($"Photo file uploaded: {PhotoFile.FileName}, Size: {PhotoFile.Length}");
                using (var ms = new MemoryStream())
                {
                    await PhotoFile.CopyToAsync(ms);
                    model.Photo = ms.ToArray();
                }
            }
            else
            {
                Console.WriteLine("No photo uploaded");
                model.Photo = null;
            }

            // Set default status if not provided
            if (string.IsNullOrEmpty(model.Status))
            {
                model.Status = "Active";
            }

            try
            {
                using (var con = new MySqlConnection(connectionString))
                {
                    await con.OpenAsync();
                    Console.WriteLine("Database connection opened successfully");

                    string query = @"
            INSERT INTO students 
            (firstname, lastname, gender, dateOfBirth, email, phone, education, status, hobbies, postalcode, country, state, city, address, password, photo)
            VALUES
            (@firstname, @lastname, @gender, @dateOfBirth, @email, @phone, @education, @status, @hobbies, @postalcode, @country, @state, @city, @address, @password, @photo)";

                    using (var cmd = new MySqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@firstname", model.FirstName ?? string.Empty);
                        cmd.Parameters.AddWithValue("@lastname", model.LastName ?? string.Empty);
                        cmd.Parameters.AddWithValue("@gender", model.Gender ?? string.Empty);
                        cmd.Parameters.AddWithValue("@dateOfBirth", model.DateOfBirth.HasValue ? (object)model.DateOfBirth.Value : DBNull.Value);
                        cmd.Parameters.AddWithValue("@email", model.Email ?? string.Empty);
                        cmd.Parameters.AddWithValue("@phone", model.Phone ?? string.Empty);
                        cmd.Parameters.AddWithValue("@education", model.Education ?? string.Empty);
                        cmd.Parameters.AddWithValue("@status", model.Status ?? "Active");
                        cmd.Parameters.AddWithValue("@hobbies", model.Hobbies ?? string.Empty);
                        cmd.Parameters.AddWithValue("@postalcode", model.PostalCode ?? string.Empty);
                        cmd.Parameters.AddWithValue("@country", model.Country ?? string.Empty);
                        cmd.Parameters.AddWithValue("@state", model.State ?? string.Empty);
                        cmd.Parameters.AddWithValue("@city", model.City ?? string.Empty);
                        cmd.Parameters.AddWithValue("@address", model.Address ?? string.Empty);
                        cmd.Parameters.AddWithValue("@password", model.Password ?? string.Empty);
                        cmd.Parameters.Add("@photo", MySqlDbType.LongBlob).Value = (object)model.Photo ?? DBNull.Value;

                        Console.WriteLine("Executing SQL command...");
                        int rowsAffected = await cmd.ExecuteNonQueryAsync();
                        Console.WriteLine($"Rows affected: {rowsAffected}");

                        if (rowsAffected > 0)
                        {
                            Console.WriteLine("Student added successfully to database!");
                            TempData["Success"] = "Student added successfully!";
                        }
                        else
                        {
                            Console.WriteLine("No rows affected - student not added!");
                            TempData["Error"] = "Failed to add student!";
                        }
                    }
                }

                return RedirectToAction("Add");
            }
            catch (Exception ex)
            {
                // Log the actual error
                Console.WriteLine($"=== DATABASE ERROR ===");
                Console.WriteLine($"Error Message: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");

                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }

                ModelState.AddModelError("", "Error: " + ex.Message);
                return View(model);
            }
        }

        public async Task<IActionResult> List()
        {
            List<Student> students = new List<Student>();

            try
            {
                using (var con = new MySqlConnection(connectionString))
                {
                    await con.OpenAsync();
                    string query = "SELECT * FROM students";

                    using (var cmd = new MySqlCommand(query, con))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            students.Add(new Student
                            {
                                Id = reader.GetInt32("id"),
                                FirstName = reader.GetString("firstname"),
                                LastName = reader.GetString("lastname"),
                                Gender = reader.GetString("gender"),
                                DateOfBirth = reader.GetDateTime("dateOfBirth"),
                                Email = reader.GetString("email"),
                                Phone = reader.GetString("phone"),
                                Education = reader.GetString("education"),
                                Status = reader["status"] != DBNull.Value ? reader.GetString("status") : "Active",
                                Country = reader["country"]?.ToString(),
                                State = reader["state"]?.ToString(),
                                City = reader["city"]?.ToString()
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ViewBag.Error = "Error: " + ex.Message;
            }

            return View(students);
        }

        public async Task<IActionResult> View(int id)
        {
            Student student = null;

            try
            {
                using (var con = new MySqlConnection(connectionString))
                {
                    await con.OpenAsync();
                    string query = @"SELECT * FROM students WHERE id = @id";

                    using (var cmd = new MySqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                student = new Student
                                {
                                    Id = reader.GetInt32("id"),
                                    FirstName = reader.GetString("firstname"),
                                    LastName = reader.GetString("lastname"),
                                    Gender = reader.GetString("gender"),
                                    DateOfBirth = reader["DateOfBirth"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(reader["DateOfBirth"]),
                                    Email = reader.GetString("email"),
                                    Phone = reader.GetString("phone"),
                                    Address = reader["address"].ToString(),
                                    City = reader["city"].ToString(),
                                    State = reader["state"].ToString(),
                                    Country = reader["country"].ToString(),
                                    PostalCode = reader["postalcode"].ToString(),
                                    Education = reader["education"].ToString(),
                                    Hobbies = reader["hobbies"].ToString(),
                                    Status = reader["status"].ToString()
                                };

                                // Load photo
                                if (reader["photo"] != DBNull.Value)
                                    student.Photo = (byte[])reader["photo"];
                            }
                        }
                    }
                }

                if (student == null)
                    return NotFound();
            }
            catch (Exception ex)
            {
                ViewBag.Error = "Error: " + ex.Message;
                return View("List");
            }

            return View(student);
        }

        // GET: /Student/Edit/1
        public async Task<IActionResult> Edit(int id)
        {
            Student student = null;

            try
            {
                using (var con = new MySqlConnection(connectionString))
                {
                    await con.OpenAsync();
                    string query = @"SELECT * FROM students WHERE id=@id";

                    using (var cmd = new MySqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                student = new Student
                                {
                                    Id = reader.GetInt32("id"),
                                    FirstName = reader["firstname"]?.ToString(),
                                    LastName = reader["lastname"]?.ToString(),
                                    Gender = reader["gender"]?.ToString(),
                                    DateOfBirth = reader["dateOfBirth"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(reader["dateOfBirth"]),
                                    Email = reader["email"]?.ToString(),
                                    Phone = reader["phone"]?.ToString(),
                                    Education = reader["education"]?.ToString(),
                                    Status = reader["status"]?.ToString(),
                                    Hobbies = reader["hobbies"]?.ToString(),
                                    PostalCode = reader["postalcode"]?.ToString(),
                                    Country = reader["country"]?.ToString(),
                                    State = reader["state"]?.ToString(),
                                    City = reader["city"]?.ToString(),
                                    Address = reader["address"]?.ToString(),
                                    Password = reader["password"]?.ToString(), // Get plain text password
                                    Photo = reader["photo"] != DBNull.Value ? (byte[])reader["photo"] : null
                                };
                            }
                        }
                    }
                }

                if (student == null)
                    return NotFound();
            }
            catch (Exception ex)
            {
                ViewBag.Error = "Error: " + ex.Message;
                return RedirectToAction("List");
            }

            // Populate hobbies list and mark selected
            SetDropdowns();
            if (!string.IsNullOrEmpty(student.Hobbies))
                student.SelectedHobbies = student.Hobbies.Split(",").Select(h => h.Trim()).ToList();

            return View(student);
        }

        // POST: /Student/Edit/1
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Student model, IFormFile PhotoFile, string[] SelectedHobbies)
        {
            SetDropdowns();

            // Combine selected hobbies
            model.Hobbies = string.Join(", ", SelectedHobbies ?? Array.Empty<string>());

            // Keep existing photo if no new one uploaded
            if (PhotoFile != null && PhotoFile.Length > 0)
            {
                using (var ms = new MemoryStream())
                {
                    await PhotoFile.CopyToAsync(ms);
                    model.Photo = ms.ToArray();
                }
            }
            else
            {
                var existing = await GetStudentById(model.Id);
                model.Photo = existing?.Photo;
            }

            // REMOVED PASSWORD HASHING - Keep existing password if new one is not provided
            if (string.IsNullOrEmpty(model.Password))
            {
                var existing = await GetStudentById(model.Id);
                model.Password = existing?.Password;
            }
            // If password is provided, save as plain text (no hashing)

            // Set default status if not provided
            if (string.IsNullOrEmpty(model.Status))
            {
                model.Status = "Active";
            }

            try
            {
                using (var con = new MySqlConnection(connectionString))
                {
                    await con.OpenAsync();
                    string query = @"
                UPDATE students
                SET firstname=@FirstName, lastname=@LastName, gender=@Gender, dateOfBirth=@DateOfBirth,
                    email=@Email, phone=@Phone, education=@Education, status=@Status,
                    hobbies=@Hobbies, postalcode=@PostalCode, country=@Country, state=@State,
                    city=@City, address=@Address, password=@Password, photo=@Photo
                WHERE id=@Id";

                    using (var cmd = new MySqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@Id", model.Id);
                        cmd.Parameters.AddWithValue("@FirstName", model.FirstName ?? "");
                        cmd.Parameters.AddWithValue("@LastName", model.LastName ?? "");
                        cmd.Parameters.AddWithValue("@Gender", model.Gender ?? "");
                        cmd.Parameters.AddWithValue("@DateOfBirth", model.DateOfBirth.HasValue ? (object)model.DateOfBirth.Value : DBNull.Value);
                        cmd.Parameters.AddWithValue("@Email", model.Email ?? "");
                        cmd.Parameters.AddWithValue("@Phone", model.Phone ?? "");
                        cmd.Parameters.AddWithValue("@Education", model.Education ?? "");
                        cmd.Parameters.AddWithValue("@Status", model.Status ?? "Active");
                        cmd.Parameters.AddWithValue("@Hobbies", model.Hobbies ?? "");
                        cmd.Parameters.AddWithValue("@PostalCode", model.PostalCode ?? "");
                        cmd.Parameters.AddWithValue("@Country", model.Country ?? "");
                        cmd.Parameters.AddWithValue("@State", model.State ?? "");
                        cmd.Parameters.AddWithValue("@City", model.City ?? "");
                        cmd.Parameters.AddWithValue("@Address", model.Address ?? "");
                        cmd.Parameters.AddWithValue("@Password", model.Password ?? ""); // Save as plain text
                        cmd.Parameters.Add("@Photo", MySqlDbType.LongBlob).Value = (object)model.Photo ?? DBNull.Value;

                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                TempData["PopupMessage"] = "Profile updated successfully!";
                return RedirectToAction("Edit", new { id = model.Id });
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", "Error: " + ex.Message);
                return View(model);
            }
        }

        // API endpoint to get student data as JSON
        [HttpGet]
        [Route("/api/students/{id}")]
        public async Task<IActionResult> GetStudent(int id)
        {
            try
            {
                Student student = null;

                using (var con = new MySqlConnection(connectionString))
                {
                    await con.OpenAsync();
                    string query = @"SELECT * FROM students WHERE id = @id";

                    using (var cmd = new MySqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@id", id);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                student = new Student
                                {
                                    Id = reader.GetInt32("id"),
                                    FirstName = reader["firstname"]?.ToString(),
                                    LastName = reader["lastname"]?.ToString(),
                                    Gender = reader["gender"]?.ToString(),
                                    DateOfBirth = reader["dateOfBirth"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(reader["dateOfBirth"]),
                                    Email = reader["email"]?.ToString(),
                                    Phone = reader["phone"]?.ToString(),
                                    Address = reader["address"]?.ToString(),
                                    City = reader["city"]?.ToString(),
                                    State = reader["state"]?.ToString(),
                                    Country = reader["country"]?.ToString(),
                                    PostalCode = reader["postalcode"]?.ToString(),
                                    Education = reader["education"]?.ToString(),
                                    Hobbies = reader["hobbies"]?.ToString(),
                                    Status = reader["status"]?.ToString(),
                                    Password = reader["password"]?.ToString() // Include plain text password
                                };

                                // Handle photo
                                if (reader["photo"] != DBNull.Value)
                                {
                                    student.Photo = (byte[])reader["photo"];
                                }
                            }
                        }
                    }
                }

                if (student == null)
                    return NotFound(new { error = "Student not found" });

                return Ok(new
                {
                    id = student.Id,
                    firstName = student.FirstName,
                    lastName = student.LastName,
                    gender = student.Gender,
                    dateOfBirth = student.DateOfBirth?.ToString("yyyy-MM-dd"),
                    email = student.Email,
                    phone = student.Phone,
                    address = student.Address,
                    city = student.City,
                    state = student.State,
                    country = student.Country,
                    postalCode = student.PostalCode,
                    education = student.Education,
                    hobbies = student.Hobbies,
                    status = student.Status,
                    password = student.Password, // Include plain text password
                    photo = student.Photo != null ? Convert.ToBase64String(student.Photo) : null
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Helper method to get student by ID
        private async Task<Student> GetStudentById(int id)
        {
            using (var con = new MySqlConnection(connectionString))
            {
                await con.OpenAsync();
                string query = @"SELECT password, photo FROM students WHERE id=@id";

                using (var cmd = new MySqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@id", id);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            return new Student
                            {
                                Password = reader["password"].ToString(), // Get plain text password
                                Photo = reader["photo"] != DBNull.Value ? (byte[])reader["photo"] : null
                            };
                        }
                    }
                }
            }
            return null;
        }

        // Helper method to set dropdown lists
        private void SetDropdowns()
        {
            ViewBag.Genders = new[] { "Male", "Female", "Other" };
            ViewBag.Qualifications = new[] { "High School", "Bachelor", "Master", "PhD" };
            ViewBag.Hobbies = new[] { "Reading", "Sports", "Music", "Traveling", "Gaming" };
            ViewBag.Statuses = new[] { "Active", "Inactive" };
        }
    }
}