// ========================================
// SIGNALR CHAT HUB CONNECTION - REAL-TIME MESSAGING
// ========================================

// Initialize SignalR connection for chat - make it global for Index.cshtml compatibility
// Initialize SignalR connection for chat - singleton pattern
if (!window.chatConnection) {
    window.chatConnection = new signalR.HubConnectionBuilder()
        .withUrl("/chatHub")
        .withAutomaticReconnect()
        .build();

    // Start the connection
    window.chatConnection.start()
        .then(() => {
            console.log("✅ Chat SignalR connected");
            sortChatList(); // Initial sort on load
        })
        .catch(err => {
            console.error("❌ Chat SignalR connection error:", err);
            // Retry connection
            setTimeout(() => window.chatConnection.start(), 5000);
        });
} else {
    console.log("ℹ️ Chat connection already exists, reusing.");
    sortChatList();
}


// Handle reconnection
chatConnection.onreconnected(() => {
    console.log("🔄 Chat SignalR reconnected");
});

chatConnection.onreconnecting(() => {
    console.log("⏳ Chat SignalR reconnecting...");
});

// ========================================
// RECEIVE MESSAGE EVENT HANDLER
// Real-time message reception with smart scroll management
// ========================================

chatConnection.on("ReceiveMessage", function (messageData) {
    console.log("📨 Received message:", messageData);

    const { senderId, receiverId, message, sentAt, filePath, imagePath, fileName, fileOriginalName } = messageData;

    // Get current user ID from session
    const currentUserElement = document.querySelector('[data-current-user-id]');
    const currentUserId = currentUserElement?.dataset.currentUserId;

    console.log(`🔍 ID Check - Sender: '${senderId}', Receiver: '${receiverId}', CurrentUser: '${currentUserId}'`);
    if (!currentUserId) console.error("⚠️ Current User ID not found in DOM!");

    const senderIdStr = String(senderId);
    const receiverIdStr = String(receiverId);
    const currentUserIdStr = String(currentUserId);

    // Determine if this message is for the currently open chat
    const isSenderCurrentChat = (typeof currentReceiverId !== 'undefined' &&
        currentReceiverType === 'user' &&
        senderIdStr == String(currentReceiverId));

    const isReceiverCurrentChat = (typeof currentReceiverId !== 'undefined' &&
        currentReceiverType === 'user' &&
        receiverIdStr == String(currentReceiverId) &&
        senderIdStr == currentUserIdStr);

    const isForCurrentChat = isSenderCurrentChat || isReceiverCurrentChat;

    if (isForCurrentChat) {
        // Message is for the currently open chat - add it to the chat window
        appendMessageToChat(messageData, senderIdStr == currentUserIdStr);

        // Smart scroll: only auto-scroll if user is at bottom
        if (typeof shouldAutoScroll !== 'undefined' && shouldAutoScroll) {
            scrollToBottom();
        }

        // Mark as read immediately since chat is open
        if (senderIdStr != currentUserIdStr) {
            fetch('/UserDashboard/MarkMessagesAsReadByUserId?otherUserId=' + senderId, {
                method: 'POST'
            }).catch(err => console.error("Error marking messages as read:", err));
        }

        // Always move to top to show it's the latest active conversation
        const chatId = (senderIdStr == currentUserIdStr) ? receiverIdStr : senderIdStr;
        moveChatToTop(chatId, 'user');
    } else {
        // Show unread dot via UnreadManager if the message is not from the current user
        if (senderIdStr != currentUserIdStr) {
            if (window.unreadManager) {
                window.unreadManager.triggerNewMessage(senderIdStr, 'user');
            }
        }

        const chatId = (senderIdStr == currentUserIdStr) ? receiverIdStr : senderIdStr;
        moveChatToTop(chatId, 'user');
    }
});

// ========================================
// RECEIVE GROUP MESSAGE EVENT HANDLER
// ========================================

chatConnection.on("ReceiveGroupMessage", function (messageData) {
    console.log("📨 Received group message:", messageData);

    const { groupId, senderId, message, senderName, sentAt, filePath, imagePath, fileName } = messageData;

    // Get current user ID
    const currentUserId = document.querySelector('[data-current-user-id]')?.dataset.currentUserId;

    // Determ if this message is for the currently open group chat
    const isForCurrentChat = (typeof currentReceiverId !== 'undefined' &&
        currentReceiverType === 'group' &&
        groupId == currentReceiverId);

    if (isForCurrentChat) {
        // Message is for the currently open group chat - add it to the chat window
        appendGroupMessageToChat(messageData, senderId == currentUserId);

        // Smart scroll: only auto-scroll if user is at bottom
        if (typeof shouldAutoScroll !== 'undefined' && shouldAutoScroll) {
            scrollToBottom();
        }

        // Mark as read immediately since chat is open
        if (senderId != currentUserId) {
            fetch('/UserDashboard/MarkGroupMessagesAsReadByGroupId?groupId=' + groupId, {
                method: 'POST'
            }).catch(err => console.error("Error marking group messages as read:", err));
        }
    } else {
        // Show unread dot via UnreadManager
        if (senderId != currentUserId) {
            if (window.unreadManager) {
                window.unreadManager.triggerNewMessage(String(groupId), 'group');
            }
        }
    }

    // Always move to top
    moveChatToTop(groupId, 'group');
});

// ========================================
// HELPER FUNCTIONS FOR MESSAGE DISPLAY
// ========================================

// Function to move chat to top of list
function moveChatToTop(chatId, type) {
    const list = document.getElementById('userList');
    if (!list) {
        console.error("❌ moveChatToTop: 'userList' element not found!");
        return;
    }

    let selector = '';
    // Ensure chatId is treated as string for selector
    const idString = String(chatId);

    if (type === 'user') {
        selector = `li.user-item[data-user-id="${idString}"]`;
    } else {
        selector = `li.user-item[data-group-id="${idString}"]`;
    }

    console.log(`🔄 Attempting to move chat to top: Type=${type}, ID=${idString}`);

    const item = list.querySelector(selector);
    if (item) {
        // Update timestamp to now to keep it at top if re-sorted
        const now = new Date().toISOString();
        item.setAttribute('data-timestamp', now);
        try {
            const key = 'chatLastActivity';
            const raw = localStorage.getItem(key);
            const map = raw ? JSON.parse(raw) : {};
            map[`${type}:${idString}`] = now;
            localStorage.setItem(key, JSON.stringify(map));
        } catch (e) { }

        // Also update the specific attributes used in the user's snippet for compatibility
        if (type === 'user') {
            item.setAttribute('data-last-message-time', now);
        } else {
            item.setAttribute('data-last-activity-time', now);
        }

        // Move to top
        list.insertBefore(item, list.firstChild);

        console.log(`✅ Moved chat ${idString} to top`);
    } else {
        console.warn(`⚠️ moveChatToTop: Item not found for ${type} ID: ${chatId}. Selector: ${selector}`);
    }
}

function appendMessageToChat(messageData, isCurrentUserSender) {
    const chatMessages = document.getElementById('chatMessages');
    if (!chatMessages) return;

    const messageDiv = document.createElement('div');
    messageDiv.className = isCurrentUserSender ? 'message sent' : 'message received';

    let messageContent = '';

    // Handle image messages
    if (messageData.imagePath) {
        messageContent = `
            <div class="message-bubble">
                <img src="${messageData.imagePath}" alt="Image" class="message-image" onclick="openImageModal('${messageData.imagePath}')" style="max-width: 200px; cursor: pointer; border-radius: 8px;" onerror="this.style.display='none'">
                <div class="message-time">${formatMessageTime(messageData.sentAt)}</div>
            </div>
        `;
    }
    // Handle file messages
    else if (messageData.filePath) {
        messageContent = `
            <div class="message-bubble">
                <a href="${messageData.filePath}" download="${messageData.fileOriginalName || messageData.fileName}" class="file-download">
                    📎 ${messageData.fileOriginalName || messageData.fileName}
                </a>
                <div class="message-time">${formatMessageTime(messageData.sentAt)}</div>
            </div>
        `;
    }
    // Handle text messages
    else if (messageData.message) {
        messageContent = `
            <div class="message-bubble">
                <div class="message-text">${escapeHtml(messageData.message)}</div>
                <div class="message-time">${formatMessageTime(messageData.sentAt)}</div>
            </div>
        `;
    }

    messageDiv.innerHTML = messageContent;
    chatMessages.appendChild(messageDiv);
}

function appendGroupMessageToChat(messageData, isCurrentUserSender) {
    const chatMessages = document.getElementById('chatMessages');
    if (!chatMessages) return;

    const messageDiv = document.createElement('div');
    messageDiv.className = isCurrentUserSender ? 'message sent' : 'message received';

    let messageContent = '';

    // Add sender name for group messages (only for received messages)
    const senderNameHtml = !isCurrentUserSender ? `<div class="message-sender">${escapeHtml(messageData.senderName)}</div>` : '';

    // Handle image messages
    if (messageData.imagePath) {
        messageContent = `
            ${senderNameHtml}
            <div class="message-bubble">
                <img src="${messageData.imagePath}" alt="Image" class="message-image" onclick="openImageModal('${messageData.imagePath}')" style="max-width: 200px; cursor: pointer; border-radius: 8px;" onerror="this.style.display='none'">
                <div class="message-time">${formatMessageTime(messageData.sentAt)}</div>
            </div>
        `;
    }
    // Handle file messages
    else if (messageData.filePath) {
        messageContent = `
            ${senderNameHtml}
            <div class="message-bubble">
                <a href="${messageData.filePath}" download="${messageData.fileName}" class="file-download">
                    📎 ${messageData.fileName}
                </a>
                <div class="message-time">${formatMessageTime(messageData.sentAt)}</div>
            </div>
        `;
    }
    // Handle text messages
    else if (messageData.message) {
        messageContent = `
            ${senderNameHtml}
            <div class="message-bubble">
                <div class="message-text">${escapeHtml(messageData.message)}</div>
                <div class="message-time">${formatMessageTime(messageData.sentAt)}</div>
            </div>
        `;
    }

    messageDiv.innerHTML = messageContent;
    chatMessages.appendChild(messageDiv);
}

function scrollToBottom() {
    const chatMessages = document.getElementById('chatMessages');
    if (chatMessages) {
        chatMessages.scrollTop = chatMessages.scrollHeight;
    }
}

function formatMessageTime(sentAt) {
    const date = new Date(sentAt);
    const hours = date.getHours().toString().padStart(2, '0');
    const minutes = date.getMinutes().toString().padStart(2, '0');
    return `${hours}:${minutes}`;
}

function escapeHtml(unsafe) {
    if (!unsafe) return '';
    return unsafe
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");
}

// ========================================
// MARK MESSAGES AS READ
// ========================================

// Function to sort chat list based on timestamp
function sortChatList() {
    const list = document.getElementById('userList');
    if (!list) return;

    // Get all list items that are chats (exclude separators if any)
    const items = Array.from(list.querySelectorAll('li.user-item'));

    // Sort items based on data-timestamp
    items.sort((a, b) => {
        const timeA = a.getAttribute('data-timestamp') || '0001-01-01';
        const timeB = b.getAttribute('data-timestamp') || '0001-01-01';
        return new Date(timeB) - new Date(timeA); // Descending order (newest first)
    });

    // Re-append items in sorted order
    items.forEach(item => list.appendChild(item));
    console.log("✅ Chat list sorted by time");
}

console.log("✅ Real-time chat script loaded");

// Run initial sort immediately to prevent FOUC
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', sortChatList);
} else {
    sortChatList();
}
