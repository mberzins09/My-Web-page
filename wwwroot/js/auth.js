window.loginUser = async (email, password) => {
    const response = await fetch('/api/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: email, password: password }),
        credentials: 'include'
    });
    return response.ok;
};

window.registerUser = async (username, email, password) => {
    const response = await fetch('/api/register', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: username, email: email, password: password }),
        credentials: 'include'
    });
    return response.ok;
};

window.logoutUser = async () => {
    await fetch('/api/logout', {
        method: 'POST',
        credentials: 'include'
    });
};