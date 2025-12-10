// ========================================
// UI FIX: HELPER FUNCTIONS FOR UNREAD DOTS AND GROUP HEADER UPDATES
// ========================================

// Helper function to update unread dot for single chats
function updateUnreadDot(userId, show) {
    console.log(`🔴 updateUnreadDot called: userId=${userId}, show=${show}`);

    // Try multiple selectors to find the user item
    const selectors = [
        `[data-user-id="${userId}"]`,
        `.user-item[data-user-id="${userId}"]`,
        `#user-${userId}`,
        `.chat-item[data-id="${userId}"]`
    ];

    let userItem = null;
    for (const selector of selectors) {
        userItem = document.querySelector(selector);
        if (userItem) {
            console.log(`✅ Found user item with selector: ${selector}`);
            break;
        }
    }

    if (!userItem) {
        console.warn(`❌ Could not find user item for userId: ${userId}`);
        console.log('Available user items:', document.querySelectorAll('[data-user-id], .user-item, .chat-item'));
        return;
    }

    let unreadDot = userItem.querySelector('.unread-dot');

    if (show) {
        // Show the dot
        if (!unreadDot) {
            console.log(`➕ Creating new unread dot for user ${userId}`);
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
                z-index: 10;
            `;
            userItem.style.position = 'relative';
            userItem.appendChild(unreadDot);
            console.log(`✅ Unread dot created for user ${userId}`);
        } else {
            console.log(`ℹ️ Unread dot already exists for user ${userId}`);
        }
    } else {
        // Hide the dot
        if (unreadDot) {
            console.log(`➖ Removing unread dot for user ${userId}`);
            unreadDot.remove();
        }
    }
}

// Helper function to update unread dot for group chats
function updateGroupUnreadDot(groupId, show) {
    console.log(`🔴 updateGroupUnreadDot called: groupId=${groupId}, show=${show}`);

    // Try multiple selectors to find the group item
    const selectors = [
        `[data-group-id="${groupId}"]`,
        `.user-item[data-group-id="${groupId}"]`,
        `#group-${groupId}`,
        `.chat-item[data-group-id="${groupId}"]`
    ];

    let groupItem = null;
    for (const selector of selectors) {
        groupItem = document.querySelector(selector);
        if (groupItem) {
            console.log(`✅ Found group item with selector: ${selector}`);
            break;
        }
    }

    if (!groupItem) {
        console.warn(`❌ Could not find group item for groupId: ${groupId}`);
        console.log('Available group items:', document.querySelectorAll('[data-group-id], .group-item'));
        return;
    }

    let unreadDot = groupItem.querySelector('.unread-dot');

    if (show) {
        // Show the dot
        if (!unreadDot) {
            console.log(`➕ Creating new unread dot for group ${groupId}`);
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
                z-index: 10;
            `;
            groupItem.style.position = 'relative';
            groupItem.appendChild(unreadDot);
            console.log(`✅ Unread dot created for group ${groupId}`);
        } else {
            console.log(`ℹ️ Unread dot already exists for group ${groupId}`);
        }
    } else {
        // Hide the dot
        if (unreadDot) {
            console.log(`➖ Removing unread dot for group ${groupId}`);
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
    console.log('🔄 Syncing unread dots...');

    console.error('❌ Error syncing unread dots:', error);
});
}

// Start syncing unread dots every 3 seconds
setInterval(syncUnreadDots, 3000);

// Also sync immediately on page load
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', syncUnreadDots);
} else {
    // Page already loaded, sync after a short delay to ensure DOM is ready
    setTimeout(syncUnreadDots, 1000);
}

console.log('✅ UI Fixes loaded - unread dot sync initialized');

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
