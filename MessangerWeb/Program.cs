using System;
using MessangerWeb.Services;
using MessangerWeb.Hubs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WebsiteApplication
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllersWithViews();
            builder.Services.AddRazorPages();

            builder.Services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromMinutes(30);
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
            });

            builder.Services.AddMemoryCache();

            // Configure Authentication FIRST
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.Name = "WebsiteApplication.Auth";
                    options.LoginPath = "/Account/Login";
                    options.AccessDeniedPath = "/Account/AccessDenied";
                    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                    options.SlidingExpiration = true;
                });

            // Add SignalR with configuration
            builder.Services.AddSignalR(options =>
            {
                options.EnableDetailedErrors = true;
                options.MaximumReceiveMessageSize = 1024000;
                options.StreamBufferCapacity = 1024 * 1024;
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            });

            // Add logging
            builder.Services.AddLogging();

            // Add all required services for UserDashboardController
            // 1. Notification Service
            builder.Services.AddScoped<INotificationService, NotificationService>();

            // 2. Video Call History Service (you need to implement this)
            // First, check if these interfaces exist. If not, you'll need to create them.
            // If you don't have these services yet, create placeholder implementations:

            // Placeholder interface and implementation for IVideoCallHistoryService
            // Add this to your MessangerWeb.Services namespace
            builder.Services.AddScoped<IVideoCallHistoryService, VideoCallHistoryService>();

            // 3. Video Call Participant Service
            builder.Services.AddScoped<IVideoCallParticipantService, VideoCallParticipantService>();

            // 4. User Service (if needed)
            // builder.Services.AddSingleton<UserService>();

            // Add HTTP context accessor
            builder.Services.AddHttpContextAccessor();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            // Use Authentication & Authorization BEFORE SignalR
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseSession();

            // Map SignalR Hubs
            app.MapHub<VideoCallHub>("/videoCallHub", options =>
            {
                options.ApplicationMaxBufferSize = 1024 * 1024;
                options.TransportMaxBufferSize = 1024 * 1024;
                options.TransportSendTimeout = TimeSpan.FromSeconds(30);
            });

            app.MapHub<ChatHub>("/chatHub", options =>
            {
                options.TransportMaxBufferSize = 1024 * 1024;
                options.TransportSendTimeout = TimeSpan.FromSeconds(30);
            });

            app.MapHub<GroupHub>("/groupHub", options =>
            {
                options.TransportMaxBufferSize = 1024 * 1024;
                options.TransportSendTimeout = TimeSpan.FromSeconds(30);
            });

            app.MapHub<StudentHub>("/studentHub");

            // Map controller routes LAST
            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");

            app.Run();
        }
    }
}