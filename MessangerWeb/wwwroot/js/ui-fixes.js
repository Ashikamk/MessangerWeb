// ========================================
// UI FIX: HELPER FUNCTIONS FOR UNREAD DOTS AND GROUP HEADER UPDATES
// ========================================

// Helper function to update unread dot for single chats
function updateUnreadDot(userId, show) {
    const userItem = document.querySelector(`[data-user-id="${userId}"]`);
    if (!userItem) return;

    let unreadDot = userItem.querySelector('.unread-dot');

    if (show) {
        // Show the dot
        if (!unreadDot) {
            unreadDot = document.createElement('span');
            unreadDot.className = 'unread-dot';
            unreadDot.style.cssText = `
                width: 10px;
                height: 10px;
                background: #ff4444;
                border-radius: 50%;
                position: absolute;
                top: 10px;
                right: 10px;
            `;
            userItem.style.position = 'relative';
            userItem.appendChild(unreadDot);
        }
    } else {
        // Hide the dot
        if (unreadDot) {
            unreadDot.remove();
        }
    }
}

// Helper function to update chat header (name and image)
function updateChatHeader(name, imageBase64) {
    // Update name in header - try multiple possible selectors
    const headerNameSelectors = [
        '.chat-header .user-name',
        '.chat-header .group-name',
        '.chat-header h3',
        '.chat-header .name',
        '#chatHeaderName'
    ];

    for (const selector of headerNameSelectors) {
        const headerName = document.querySelector(selector);
        if (headerName) {
            headerName.textContent = name;
            break;
        }
    }

    // Update image in header - try multiple possible selectors
    if (imageBase64) {
        const headerImageSelectors = [
            '.chat-header .user-avatar',
            '.chat-header .group-avatar',
            '.chat-header img',
            '#chatHeaderImage'
        ];

        for (const selector of headerImageSelectors) {
            const headerImage = document.querySelector(selector);
            if (headerImage) {
                headerImage.src = `data:image/jpeg;base64,${imageBase64}`;
                break;
            }
        }
    }
}

// ========================================
// AUTOMATIC UNREAD DOT SYNCHRONIZATION
// Polls the backend and updates unread dots automatically
// ========================================
function syncUnreadDots() {
    fetch('/UserDashboard/GetUnreadMessagesCount')
        .then(response => response.json())
        .then(data => {
            if (data.success && data.unreadMessages) {
                // Get all user items
                const allUserItems = document.querySelectorAll('[data-user-id]');

                // First, remove all unread dots
                allUserItems.forEach(userItem => {
                    const userId = userItem.getAttribute('data-user-id');
                    const unreadDot = userItem.querySelector('.unread-dot');
                    if (unreadDot) {
                        unreadDot.remove();
                    }
                });

                // Then, add unread dots for users with unread messages
                Object.keys(data.unreadMessages).forEach(userId => {
                    const count = data.unreadMessages[userId];
                    if (count > 0) {
                        updateUnreadDot(userId, true);
                    }
                });
            }
        })
        .catch(error => {
            console.log('Error syncing unread dots:', error);
        });
}

// Start syncing unread dots every 3 seconds
setInterval(syncUnreadDots, 3000);

// Also sync immediately on page load
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', syncUnreadDots);
} else {
    syncUnreadDots();
}

// ========================================
// SIGNALR EVENT HANDLER: GROUP UPDATED
// Add this where your other connection.on handlers are
// ========================================
if (typeof connection !== 'undefined' && connection) {
    connection.on("GroupUpdated", function (data) {
        console.log("Group updated:", data);

        // Update group name in sidebar
        const groupItem = document.querySelector(`[data-group-id="${data.groupId}"]`);
        if (groupItem) {
            const groupNameElement = groupItem.querySelector('.user-name, .group-name');
            if (groupNameElement) {
                groupNameElement.textContent = data.groupName;
            }

            // Update group image if provided
            if (data.groupImageBase64) {
                const groupImageElement = groupItem.querySelector('.user-avatar, .group-avatar img');
                if (groupImageElement) {
                    groupImageElement.src = `data:image/jpeg;base64,${data.groupImageBase64}`;
                }
            }
        }

        // Update message panel header if this group is currently open
        if (typeof currentReceiverType !== 'undefined' && typeof currentReceiverId !== 'undefined') {
            if (currentReceiverType === 'group' && currentReceiverId == data.groupId) {
                updateChatHeader(data.groupName, data.groupImageBase64);
            }
        }
    });
}
