// unread-manager.js - Updated for real-time
class UnreadManager {
    constructor() {
        this.unreadCounts = new Map(); // chatId -> count
        this.currentChat = null;
        this.currentChatType = null;
        this.initialize();
    }

    initialize() {
        this.loadFromStorage();
        this.updateUI();

        // Subscribe to real-time updates from SignalR
        if (window.chatHubConnection) {
            window.chatHubConnection.on('ReceiveNewMessage', (senderId, messageId) => {
                this.handleNewMessage(senderId, 'user');
            });

            window.chatHubConnection.on('ReceiveNewGroupMessage', (groupId, senderId, messageId) => {
                const currentUserId = document.getElementById('currentUserId').value;
                if (senderId !== currentUserId) {
                    this.handleNewMessage(groupId, 'group');
                }
            });

            window.chatHubConnection.on('UpdateChatList', () => {
                this.syncWithServer();
            });
        }
    }

    handleNewMessage(chatId, type) {
        const currentChatId = window.currentReceiverId;
        const currentChatType = window.currentReceiverType;

        // If this message is not for the currently open chat, increment unread count
        if (currentChatId !== chatId || currentChatType !== type) {
            this.incrementUnread(chatId, type);
        } else {
            // If it is for the current chat, mark as read
            this.markAsRead(chatId, type);
        }
    }

    incrementUnread(chatId, type) {
        const key = `${type}:${chatId}`;
        const current = this.unreadCounts.get(key) || 0;
        this.unreadCounts.set(key, current + 1);
        this.updateUI();
        this.saveToStorage();
    }

    markAsRead(chatId, type) {
        const key = `${type}:${chatId}`;
        this.unreadCounts.delete(key);
        this.updateUI();
        this.saveToStorage();
    }

    setCurrentChat(chatId, type) {
        this.currentChat = chatId;
        this.currentChatType = type;
        this.markAsRead(chatId, type);
    }

    updateUI() {
        // Update all chat items
        document.querySelectorAll('.user-item').forEach(item => {
            const chatId = item.getAttribute('data-user-id') || item.getAttribute('data-group-id');
            const type = item.getAttribute('data-user-id') ? 'user' : 'group';

            if (chatId) {
                const key = `${type}:${chatId}`;
                const count = this.unreadCounts.get(key) || 0;

                // Remove existing dot
                const existingDot = item.querySelector('.unread-dot');
                if (existingDot) {
                    existingDot.remove();
                }

                // Add new dot if unread
                if (count > 0) {
                    const dot = document.createElement('div');
                    dot.className = 'unread-dot';
                    dot.textContent = count > 9 ? '9+' : count;
                    dot.style.cssText = `
                        width: ${count > 9 ? '22px' : '18px'};
                        height: ${count > 9 ? '22px' : '18px'};
                        background-color: #f56565;
                        border-radius: 50%;
                        position: absolute;
                        right: 15px;
                        top: 50%;
                        transform: translateY(-50%);
                        color: white;
                        font-size: 10px;
                        display: flex;
                        align-items: center;
                        justify-content: center;
                        font-weight: bold;
                        box-shadow: 0 0 5px rgba(245, 101, 101, 0.5);
                        pointer-events: none;
                        z-index: 10;
                    `;
                    item.style.position = 'relative';
                    item.appendChild(dot);
                }
            }
        });
    }

    syncWithServer() {
        fetch('/UserDashboard/GetUnreadCounts')
            .then(response => response.json())
            .then(data => {
                if (data.success) {
                    this.unreadCounts.clear();
                    data.unreadCounts.forEach(item => {
                        const key = `${item.type}:${item.id}`;
                        this.unreadCounts.set(key, item.count);
                    });
                    this.updateUI();
                }
            });
    }

    saveToStorage() {
        const data = Array.from(this.unreadCounts.entries()).map(([key, count]) => ({
            key,
            count
        }));
        localStorage.setItem('unreadCounts', JSON.stringify(data));
    }

    loadFromStorage() {
        try {
            const data = JSON.parse(localStorage.getItem('unreadCounts') || '[]');
            data.forEach(item => {
                this.unreadCounts.set(item.key, item.count);
            });
        } catch (e) {
            console.error('Error loading unread counts:', e);
        }
    }
}

// Create global instance
window.unreadManager = new UnreadManager();