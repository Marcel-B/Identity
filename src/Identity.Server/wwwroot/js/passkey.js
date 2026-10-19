// Browser side of the passkey ceremonies. The server creates the options and verifies the result.
const Passkey = (() => {
    const supported = () =>
        typeof PublicKeyCredential !== "undefined" &&
        typeof PublicKeyCredential.parseCreationOptionsFromJSON === "function" &&
        typeof PublicKeyCredential.parseRequestOptionsFromJSON === "function";

    const antiforgeryToken = form => form.querySelector("input[name='__RequestVerificationToken']").value;

    async function fetchOptions(url, form) {
        const response = await fetch(url, {
            method: "POST",
            headers: { "RequestVerificationToken": antiforgeryToken(form) },
            credentials: "same-origin",
        });
        if (!response.ok) {
            throw new Error(`Server responded with ${response.status}`);
        }
        return response.json();
    }

    function setupLogin(button, form, optionsUrl) {
        if (!supported()) {
            return;
        }

        button.hidden = false;
        button.addEventListener("click", async () => {
            try {
                const options = await fetchOptions(optionsUrl, form);
                const credential = await navigator.credentials.get({
                    publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(options),
                });
                form.querySelector("input[name='PasskeyCredential']").value = JSON.stringify(credential);
                form.submit();
            } catch (error) {
                console.warn("Passkey sign-in cancelled or failed", error);
            }
        });
    }

    function setupRegistration(button, form, optionsUrl, unsupportedMessage) {
        if (!supported()) {
            unsupportedMessage.hidden = false;
            button.disabled = true;
            return;
        }

        button.addEventListener("click", async () => {
            try {
                const options = await fetchOptions(optionsUrl, form);
                const credential = await navigator.credentials.create({
                    publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(options),
                });
                form.querySelector("input[name='Credential']").value = JSON.stringify(credential);
                form.submit();
            } catch (error) {
                console.warn("Passkey registration cancelled or failed", error);
            }
        });
    }

    return { setupLogin, setupRegistration };
})();
