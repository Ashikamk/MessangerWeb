using System;
using MessangerWeb.Services;
using MessangerWeb.Hubs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WebsiteApplication.Services;

namespace WebsiteApplication
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllersWithViews();

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
                options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
                options.StreamBufferCapacity = 1024 * 1024; // 1MB
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            });

            // Add logging
            builder.Services.AddLogging();

            // Add your custom services

            builder.Services.AddScoped<IVideoCallHistoryService, VideoCallHistoryService>();
            builder.Services.AddScoped<IVideoCallParticipantService, VideoCallParticipantService>();

            // Add these services
            builder.Services.AddSingleton<UserService>();
            builder.Services.AddSingleton<NotificationService>();

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

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=Home}/{action=Index}/{id?}");

                // Map SignalR Hubs with increased buffer sizes
                endpoints.MapHub<VideoCallHub>("/videoCallHub", options =>
                {
                    options.ApplicationMaxBufferSize = 1024 * 1024; // 1MB
                });

                endpoints.MapHub<ChatHub>("/chatHub");
                    options.TransportMaxBufferSize = 1024 * 1024; // 1MB
                    options.TransportSendTimeout = TimeSpan.FromSeconds(30);
                });
            });
            app.Urls.Add("http://0.0.0.0:5000");

            app.Run();
        }
    }
}